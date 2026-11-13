using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Evaluation;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Prompts;

/// <summary>A15 projections and A16 accuracy, always using PromptAccuracyCalculator.</summary>
public static class PromptTestQueries
{
    public static IQueryable<PromptTestRunDto> QueryRuns(this SmartCvDbContext db, int? evaluationSetId = null)
    {
        var runs = db.EvaluationRuns.AsNoTracking().Where(r => r.RunType == RunTypes.PromptTest);
        if (evaluationSetId is not null) runs = runs.Where(r => r.EvaluationSetId == evaluationSetId);
        return runs.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RunId)
            .Select(r => new PromptTestRunDto
            {
                RunId = r.RunId, PromptVersionId = r.PromptVersionId,
                PromptVersionName = r.PromptVersion.VersionName,
                EvaluationSetId = r.EvaluationSetId!.Value,
                EvaluationSetName = r.EvaluationSet!.Name,
                ModelName = r.ModelName, Temperature = r.Temperature,
                KeywordWeight = r.KeywordWeight, AiWeight = r.AiWeight, PassThreshold = r.PassThreshold,
                Status = r.Status, TotalItems = r.TotalItems, ProcessedItems = r.ProcessedItems,
                FailedItems = r.FailedItems,
                CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
                CompletedAt = r.CompletedAt == null ? null : DateTime.SpecifyKind(r.CompletedAt.Value, DateTimeKind.Utc),
                AccuracyPercent = r.Status == RunStatuses.Completed ? r.AccuracyPercent : null
            });
    }

    /// <summary>
    /// Read repair of the Completed cache shares the set lock used by label writes and worker completion.
    /// One prediction query per set covers all its runs; there is no query per run.
    /// </summary>
    public static async Task<List<PromptTestRunDto>> LoadSummariesAsync(
        this SmartCvDbContext db, int? evaluationSetId, CancellationToken ct)
    {
        var query = db.EvaluationRuns.AsNoTracking().Where(r => r.RunType == RunTypes.PromptTest);
        if (evaluationSetId is not null) query = query.Where(r => r.EvaluationSetId == evaluationSetId);
        var setIds = await query.Select(r => r.EvaluationSetId!.Value).Distinct().OrderBy(id => id).ToListAsync(ct);
        var summaries = new List<PromptTestRunDto>();
        foreach (var setId in setIds)
        {
            await using var transaction = await EvaluationSetWriteScope.BeginAsync(db, setId, ct);
            var infos = await RunInfos(db, setId).ToListAsync(ct);
            var completedIds = infos.Where(i => i.Run.Status == RunStatuses.Completed).Select(i => i.Run.RunId).ToList();
            var predictions = await LoadPredictionsAsync(db, completedIds, setId, ct);
            var byRun = predictions.ToLookup(p => p.RunId);
            var changed = false;
            foreach (var info in infos)
            {
                var accuracy = info.Run.Status == RunStatuses.Completed
                    ? PromptAccuracyCalculator.Calculate(byRun[info.Run.RunId].Select(p => p.Prediction)) : null;
                if (accuracy is not null && info.Run.AccuracyPercent != accuracy.AccuracyPercent)
                {
                    info.Run.AccuracyPercent = accuracy.AccuracyPercent;
                    changed = true;
                }
                summaries.Add(ToSummary(info, accuracy));
            }
            if (changed) await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        return summaries.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RunId).ToList();
    }

    public static async Task<PromptTestProgressDto?> LoadProgressAsync(this SmartCvDbContext db, int runId, CancellationToken ct)
    {
        var setId = await db.EvaluationRuns.AsNoTracking()
            .Where(r => r.RunId == runId && r.RunType == RunTypes.PromptTest)
            .Select(r => r.EvaluationSetId).SingleOrDefaultAsync(ct);
        if (setId is null) return null;
        await using var transaction = await EvaluationSetWriteScope.BeginAsync(db, setId.Value, ct);
        var info = await RunInfos(db, setId.Value, runId).SingleOrDefaultAsync(ct);
        if (info is null) return null;
        var run = info.Run;
        var items = await db.Evaluations.AsNoTracking().Where(e => e.RunId == runId)
            .OrderBy(e => e.EvaluationId)
            .Select(e => new PromptTestEvaluationDto
            {
                EvaluationId = e.EvaluationId, ApplicationId = e.ApplicationId, CvId = e.Application.CvId,
                CandidateName = e.Application.Cv.AnonymizedAt != null || e.Application.Cv.Candidate == null ? null : e.Application.Cv.Candidate.FullName,
                FileName = e.Application.Cv.AnonymizedAt == null ? e.Application.Cv.OriginalFileName : SmartCv.Core.Privacy.RetentionRules.AnonymizedFileName, JobId = e.Application.JobId, JobTitle = e.Application.Job.Title,
                HrIsPass = e.Application.HrLabels.Where(h => h.EvaluationSetId == setId)
                    .Select(h => (bool?)h.IsPass).FirstOrDefault(),
                HrNote = e.Application.HrLabels.Where(h => h.EvaluationSetId == setId)
                    .Select(h => h.Note).FirstOrDefault(),
                Status = e.Status, ExtractionStatus = e.Application.Cv.ExtractionStatus,
                KeywordScore = e.KeywordScore, AiScore = e.AiScore, FinalScore = e.FinalScore,
                PredictedPass = e.PredictedPass, ScoreSource = e.ScoreSource, FitLabel = e.FitLabel,
                Reasoning = e.Application.Cv.AnonymizedAt == null ? e.Reasoning : null, ErrorMessage = e.Application.Cv.AnonymizedAt == null ? e.ErrorMessage : null
            }).ToListAsync(ct);
        var accuracy = run.Status == RunStatuses.Completed
            ? PromptAccuracyCalculator.Calculate(items.Where(i => i.HrIsPass is not null).Select(ToPrediction)) : null;
        if (accuracy is not null && run.AccuracyPercent != accuracy.AccuracyPercent)
        {
            run.AccuracyPercent = accuracy.AccuracyPercent;
            await db.SaveChangesAsync(ct);
        }
        var result = new PromptTestProgressDto
        {
            RunId = run.RunId, PromptVersionId = run.PromptVersionId, PromptVersionName = info.PromptVersionName,
            EvaluationSetId = setId.Value, EvaluationSetName = info.EvaluationSetName,
            ModelName = run.ModelName, Temperature = run.Temperature, KeywordWeight = run.KeywordWeight,
            AiWeight = run.AiWeight, PassThreshold = run.PassThreshold, Status = run.Status,
            TotalItems = run.TotalItems, ProcessedItems = run.ProcessedItems, FailedItems = run.FailedItems,
            CreatedAt = Utc(run.CreatedAt), CompletedAt = run.CompletedAt is { } completed ? Utc(completed) : null,
            AccuracyPercent = accuracy?.AccuracyPercent,
            TotalLabeledCount = accuracy?.Total, CorrectCount = accuracy?.Correct, MissedCount = accuracy?.Missed,
            WrongPickCount = accuracy?.WrongPick, UnscoredCount = accuracy?.Unscored, KeywordOnlyCount = accuracy?.KeywordOnly,
            Matrix = ToMatrix(accuracy), AccuracyLow = accuracy?.Confidence95?.Low, AccuracyHigh = accuracy?.Confidence95?.High,
            Items = items.Select(WithOutcome).ToList()
        };
        if (transaction is not null) await transaction.CommitAsync(ct);
        return result;
    }

    public static async Task<AccuracyResult> CalculateAccuracyAsync(
        this SmartCvDbContext db, int runId, int evaluationSetId, CancellationToken ct)
    {
        var rows = await LoadPredictionsAsync(db, [runId], evaluationSetId, ct);
        return PromptAccuracyCalculator.Calculate(rows.Select(r => r.Prediction));
    }

    /// <summary>Caller holds EvaluationSetWriteScope and has saved the changed HR labels in that transaction.</summary>
    public static async Task<List<int>> RecalculateSetAccuracyAsync(this SmartCvDbContext db, int setId, CancellationToken ct)
    {
        var runs = await db.EvaluationRuns.Where(r => r.RunType == RunTypes.PromptTest
            && r.EvaluationSetId == setId && r.Status == RunStatuses.Completed).ToListAsync(ct);
        if (runs.Count == 0) return [];
        var predictions = await LoadPredictionsAsync(db, runs.Select(r => r.RunId).ToList(), setId, ct);
        var byRun = predictions.ToLookup(p => p.RunId);
        var changed = new List<int>();
        foreach (var run in runs)
        {
            var accuracy = PromptAccuracyCalculator.Calculate(byRun[run.RunId].Select(r => r.Prediction));
            if (run.AccuracyPercent == accuracy.AccuracyPercent) continue;
            run.AccuracyPercent = accuracy.AccuracyPercent;
            changed.Add(run.RunId);
        }
        if (changed.Count > 0) await db.SaveChangesAsync(ct);
        return changed.OrderBy(id => id).ToList();
    }

    /// <summary>Caller holds the set lock and has saved evaluation/progress changes. Never recalculates a running run.</summary>
    public static async Task<AccuracyResult?> SaveCompletedAccuracyAsync(this SmartCvDbContext db, int runId, CancellationToken ct)
    {
        var run = await db.EvaluationRuns.FirstOrDefaultAsync(r => r.RunId == runId, ct);
        if (run is null || run.RunType != RunTypes.PromptTest || run.Status != RunStatuses.Completed
            || run.EvaluationSetId is not { } setId) return null;
        var accuracy = await db.CalculateAccuracyAsync(runId, setId, ct);
        if (run.AccuracyPercent != accuracy.AccuracyPercent)
        {
            run.AccuracyPercent = accuracy.AccuracyPercent;
            await db.SaveChangesAsync(ct);
        }
        return accuracy;
    }

    private sealed record RunInfo(EvaluationRun Run, string PromptVersionName, string EvaluationSetName);
    private sealed record RunPrediction(int RunId, LabeledPrediction Prediction);

    private static IQueryable<RunInfo> RunInfos(SmartCvDbContext db, int setId, int? runId = null)
    {
        var runs = db.EvaluationRuns.Where(r => r.RunType == RunTypes.PromptTest && r.EvaluationSetId == setId);
        if (runId is not null) runs = runs.Where(r => r.RunId == runId);
        return runs.Select(r => new RunInfo(r, r.PromptVersion.VersionName, r.EvaluationSet!.Name));
    }

    private static async Task<List<RunPrediction>> LoadPredictionsAsync(
        SmartCvDbContext db, IReadOnlyCollection<int> runIds, int setId, CancellationToken ct)
    {
        if (runIds.Count == 0) return [];
        var rows = await db.Evaluations.AsNoTracking()
            .Where(e => runIds.Contains(e.RunId) && e.Run.RunType == RunTypes.PromptTest && e.Run.EvaluationSetId == setId)
            .Join(db.HrLabels.AsNoTracking().Where(h => h.EvaluationSetId == setId),
                e => e.ApplicationId, h => h.ApplicationId,
                (e, h) => new { e.RunId, e.ApplicationId, HrIsPass = h.IsPass, e.Status, e.PredictedPass, e.ScoreSource })
            .ToListAsync(ct);
        return rows.Select(r => new RunPrediction(r.RunId,
            new LabeledPrediction(r.ApplicationId, r.HrIsPass, r.Status, r.PredictedPass, r.ScoreSource))).ToList();
    }

    private static PromptTestRunDto ToSummary(RunInfo info, AccuracyResult? accuracy)
    {
        var run = info.Run;
        return new PromptTestRunDto
        {
            RunId = run.RunId, PromptVersionId = run.PromptVersionId, PromptVersionName = info.PromptVersionName,
            EvaluationSetId = run.EvaluationSetId!.Value, EvaluationSetName = info.EvaluationSetName,
            ModelName = run.ModelName, Temperature = run.Temperature, KeywordWeight = run.KeywordWeight,
            AiWeight = run.AiWeight, PassThreshold = run.PassThreshold, Status = run.Status,
            TotalItems = run.TotalItems, ProcessedItems = run.ProcessedItems, FailedItems = run.FailedItems,
            CreatedAt = Utc(run.CreatedAt), CompletedAt = run.CompletedAt is { } completed ? Utc(completed) : null,
            AccuracyPercent = accuracy?.AccuracyPercent, TotalLabeledCount = accuracy?.Total,
            CorrectCount = accuracy?.Correct, MissedCount = accuracy?.Missed, WrongPickCount = accuracy?.WrongPick,
            UnscoredCount = accuracy?.Unscored, KeywordOnlyCount = accuracy?.KeywordOnly,
            Matrix = ToMatrix(accuracy), AccuracyLow = accuracy?.Confidence95?.Low, AccuracyHigh = accuracy?.Confidence95?.High
        };
    }

    /// <summary>A41: ma trận 2×2 từ đúng kết quả A16 đã tính (không truy vấn thêm). null khi chưa có tỷ lệ đúng.</summary>
    internal static ConfusionMatrixDto? ToMatrix(AccuracyResult? accuracy) => accuracy is null ? null : new ConfusionMatrixDto
    {
        TruePass = accuracy.TruePass, Missed = accuracy.Missed, WrongPick = accuracy.WrongPick,
        TrueFail = accuracy.TrueFail, Unscored = accuracy.Unscored
    };

    private static LabeledPrediction ToPrediction(PromptTestEvaluationDto item) =>
        new(item.ApplicationId, item.HrIsPass!.Value, item.Status, item.PredictedPass, item.ScoreSource);

    private static PromptTestEvaluationDto WithOutcome(PromptTestEvaluationDto item)
    {
        var outcome = item.HrIsPass is null ? PredictionOutcome.Unscored
            : PromptAccuracyCalculator.Classify(ToPrediction(item));
        return new PromptTestEvaluationDto
        {
            EvaluationId = item.EvaluationId, ApplicationId = item.ApplicationId, CvId = item.CvId,
            CandidateName = item.CandidateName, FileName = item.FileName, JobId = item.JobId, JobTitle = item.JobTitle,
            HrIsPass = item.HrIsPass, HrNote = item.HrNote, Status = item.Status, ExtractionStatus = item.ExtractionStatus,
            KeywordScore = item.KeywordScore, AiScore = item.AiScore, FinalScore = item.FinalScore,
            PredictedPass = item.PredictedPass, ScoreSource = item.ScoreSource, FitLabel = item.FitLabel,
            Reasoning = item.Reasoning, ErrorMessage = item.ErrorMessage,
            IsMatch = PromptAccuracyCalculator.IsMatch(outcome), MismatchType = PromptAccuracyCalculator.MismatchType(outcome)
        };
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
