using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Evaluation;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Prompts;

/// <summary>Thông tin một lượt chạy cần cho A17 (mọi RunType, để API báo đúng lỗi khi chọn nhầm lượt Screening).</summary>
public sealed record PromptCompareRunInfo(
    int RunId,
    string RunType,
    string Status,
    int PromptVersionId,
    string PromptVersionName,
    int? EvaluationSetId,
    string? EvaluationSetName,
    string ModelName,
    decimal Temperature,
    decimal KeywordWeight,
    decimal AiWeight,
    decimal PassThreshold,
    int TotalItems,
    int ProcessedItems,
    int FailedItems,
    DateTime CreatedAt,
    DateTime? CompletedAt);

/// <summary>
/// A17: đọc hai lượt PromptTest ĐÃ LƯU và dựng <see cref="PromptCompareDto"/>.
/// Chỉ đọc: không gọi Gemini, không chấm lại, không sửa nhãn HR, không ghi cache tỷ lệ đúng.
/// Kết quả từng CV và nhãn HR được đọc dưới khóa bộ đánh giá của A15/A16 để không lẫn nhãn cũ/mới khi HR đang sửa nhãn.
/// </summary>
public static class PromptCompareQueries
{
    public static Task<List<PromptCompareRunInfo>> LoadCompareRunsAsync(
        this SmartCvDbContext db, IReadOnlyCollection<int> runIds, CancellationToken ct) =>
        Project(db.EvaluationRuns.AsNoTracking().Where(r => runIds.Contains(r.RunId))).ToListAsync(ct);

    /// <summary>
    /// Lượt PromptTest HOÀN THÀNH gần nhất của một phiên bản trên một bộ. Thứ tự giống "tỷ lệ đúng gần nhất" của A14
    /// (CompletedAt giảm dần, rồi RunId giảm dần) nên luôn xác định và khớp số hiển thị ở danh sách prompt.
    /// Lượt Queued/Running/Failed không bao giờ được chọn thay.
    /// </summary>
    public static Task<PromptCompareRunInfo?> LatestCompletedPromptTestAsync(
        this SmartCvDbContext db, int promptVersionId, int evaluationSetId, CancellationToken ct) =>
        Project(db.EvaluationRuns.AsNoTracking()
                .Where(r => r.RunType == RunTypes.PromptTest && r.PromptVersionId == promptVersionId
                            && r.EvaluationSetId == evaluationSetId && r.Status == RunStatuses.Completed)
                .OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.RunId))
            .FirstOrDefaultAsync(ct);

    /// <summary>Lượt đang chờ / đang chạy gần nhất (chỉ để báo cho người dùng biết vì sao chưa có kết quả).</summary>
    public static Task<PromptCompareRunInfo?> LatestActivePromptTestAsync(
        this SmartCvDbContext db, int promptVersionId, int evaluationSetId, CancellationToken ct) =>
        Project(db.EvaluationRuns.AsNoTracking()
                .Where(r => r.RunType == RunTypes.PromptTest && r.PromptVersionId == promptVersionId
                            && r.EvaluationSetId == evaluationSetId
                            && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running))
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RunId))
            .FirstOrDefaultAsync(ct);

    /// <summary>Người gọi đã kiểm tra: hai lượt khác nhau, đều là PromptTest có bộ đánh giá.</summary>
    public static async Task<PromptCompareDto> BuildPromptCompareAsync(
        this SmartCvDbContext db, PromptCompareRunInfo baseRun, PromptCompareRunInfo candidateRun, CancellationToken ct)
    {
        var baseSet = baseRun.EvaluationSetId ?? throw new ArgumentException("Lượt gốc không có bộ đánh giá.", nameof(baseRun));
        var candidateSet = candidateRun.EvaluationSetId ?? throw new ArgumentException("Lượt thử không có bộ đánh giá.", nameof(candidateRun));

        // Cùng bộ: một lần đọc dưới một khóa, hai bên đối chiếu cùng một bản nhãn HR.
        // Khác bộ (luôn NotComparable): mỗi bên đọc nhãn của bộ mình, dưới khóa của bộ đó.
        var rows = baseSet == candidateSet
            ? await LoadRowsAsync(db, baseSet, [baseRun.RunId, candidateRun.RunId], ct)
            : [.. await LoadRowsAsync(db, baseSet, [baseRun.RunId], ct), .. await LoadRowsAsync(db, candidateSet, [candidateRun.RunId], ct)];

        var baseRows = rows.Where(r => r.RunId == baseRun.RunId).ToDictionary(r => r.ApplicationId);
        var candidateRows = rows.Where(r => r.RunId == candidateRun.RunId).ToDictionary(r => r.ApplicationId);

        string Describe(int applicationId)
        {
            var row = candidateRows.GetValueOrDefault(applicationId) ?? baseRows.GetValueOrDefault(applicationId);
            if (row is null) return $"hồ sơ #{applicationId}";
            var name = string.IsNullOrWhiteSpace(row.CandidateName) ? row.FileName : row.CandidateName;
            return $"{name} (hồ sơ #{applicationId})";
        }

        var comparison = PromptComparer.Compare(Side(baseRun, baseRows.Values), Side(candidateRun, candidateRows.Values), Describe);

        var items = comparison.Items.Select(i =>
        {
            var b = baseRows[i.ApplicationId];
            var c = candidateRows[i.ApplicationId];
            return new PromptCompareItemDto
            {
                ApplicationId = i.ApplicationId,
                CandidateName = c.CandidateName ?? b.CandidateName,
                FileName = c.FileName,
                JobTitle = c.JobTitle,
                HrIsPass = i.HrIsPass,
                HrNote = c.HrNote ?? b.HrNote,
                BaseEvaluationId = b.EvaluationId,
                BasePredictedPass = b.PredictedPass,
                BaseScore = b.FinalScore,
                BaseIsMatch = PromptAccuracyCalculator.IsMatch(i.BaseOutcome),
                CandidateEvaluationId = c.EvaluationId,
                CandidatePredictedPass = c.PredictedPass,
                CandidateScore = c.FinalScore,
                CandidateIsMatch = PromptAccuracyCalculator.IsMatch(i.CandidateOutcome),
                ScoreDelta = b.FinalScore is { } before && c.FinalScore is { } after ? after - before : null,
                Change = i.Change.ToString()
            };
        })
        // CV thay đổi đứng đầu (Fixed → Broken → Unscored → Unchanged), sau đó theo job và ApplicationId: luôn cùng thứ tự
        .OrderBy(i => ChangeRank(i.Change)).ThenBy(i => i.JobTitle, StringComparer.Ordinal).ThenBy(i => i.ApplicationId)
        .ToList();

        return new PromptCompareDto
        {
            // Tóm tắt từng bên tính trên các CV CHUNG; khi danh sách CV lệch nhau, số này khác số của từng lượt riêng lẻ (có cảnh báo)
            Base = Summary(baseRun, comparison.Base),
            Candidate = Summary(candidateRun, comparison.Candidate),
            AccuracyDelta = comparison.AccuracyDelta,
            CorrectDelta = comparison.CorrectDelta,
            MissedDelta = comparison.MissedDelta,
            WrongPickDelta = comparison.WrongPickDelta,
            FixedCount = comparison.FixedCount,
            BrokenCount = comparison.BrokenCount,
            UnchangedCount = comparison.UnchangedCount,
            UnscoredCount = comparison.UnscoredCount,
            Verdict = comparison.Verdict,
            Summary = comparison.Summary,
            IsFairComparison = comparison.IsFairComparison,
            Warnings = [.. comparison.Warnings],
            Conclusive = comparison.Conclusive,             // A41
            ConclusionNote = comparison.ConclusionNote,     // A41
            Items = items
        };
    }

    private sealed class CompareRow
    {
        public int RunId { get; init; }
        public int EvaluationId { get; init; }
        public int ApplicationId { get; init; }
        public string Status { get; init; } = string.Empty;
        public bool? PredictedPass { get; init; }
        public decimal? FinalScore { get; init; }
        public string? ScoreSource { get; init; }
        public string? CandidateName { get; init; }
        public string FileName { get; init; } = string.Empty;
        public string JobTitle { get; init; } = string.Empty;
        public bool? HrIsPass { get; init; }   // null: HR đã bỏ nhãn → không tính (giống A16)
        public string? HrNote { get; init; }
    }

    private static async Task<List<CompareRow>> LoadRowsAsync(SmartCvDbContext db, int setId, int[] runIds, CancellationToken ct)
    {
        await using var transaction = await EvaluationSetWriteScope.BeginAsync(db, setId, ct);
        var rows = await db.Evaluations.AsNoTracking()
            .Where(e => runIds.Contains(e.RunId))
            .OrderBy(e => e.RunId).ThenBy(e => e.ApplicationId)
            .Select(e => new CompareRow
            {
                RunId = e.RunId, EvaluationId = e.EvaluationId, ApplicationId = e.ApplicationId,
                Status = e.Status, PredictedPass = e.PredictedPass, FinalScore = e.FinalScore, ScoreSource = e.ScoreSource,
                CandidateName = e.Application.Cv.AnonymizedAt != null || e.Application.Cv.Candidate == null ? null : e.Application.Cv.Candidate.FullName,
                FileName = e.Application.Cv.AnonymizedAt == null ? e.Application.Cv.OriginalFileName : SmartCv.Core.Privacy.RetentionRules.AnonymizedFileName, JobTitle = e.Application.Job.Title,
                HrIsPass = e.Application.HrLabels.Where(h => h.EvaluationSetId == setId)
                    .Select(h => (bool?)h.IsPass).FirstOrDefault(),
                HrNote = e.Application.HrLabels.Where(h => h.EvaluationSetId == setId)
                    .Select(h => h.Note).FirstOrDefault()
            }).ToListAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return rows;
    }

    private static CompareRun Side(PromptCompareRunInfo info, IEnumerable<CompareRow> rows) => new(
        info.RunId, info.PromptVersionName, info.Status,
        new RunSettings(info.EvaluationSetId, info.ModelName, info.Temperature, info.KeywordWeight, info.AiWeight, info.PassThreshold),
        rows.Where(r => r.HrIsPass is not null)
            .Select(r => new LabeledPrediction(r.ApplicationId, r.HrIsPass!.Value, r.Status, r.PredictedPass, r.ScoreSource))
            .ToList());

    private static PromptTestSummaryDto Summary(PromptCompareRunInfo info, AccuracyResult accuracy) => new()
    {
        RunId = info.RunId, PromptVersionId = info.PromptVersionId, PromptVersionName = info.PromptVersionName,
        EvaluationSetId = info.EvaluationSetId ?? 0, EvaluationSetName = info.EvaluationSetName ?? string.Empty,
        ModelName = info.ModelName, Status = info.Status,
        TotalItems = info.TotalItems, ProcessedItems = info.ProcessedItems, FailedItems = info.FailedItems,
        AccuracyPercent = accuracy.AccuracyPercent, CorrectCount = accuracy.Correct,
        MissedCount = accuracy.Missed, WrongPickCount = accuracy.WrongPick,
        // A41: cùng phạm vi CV chung với AccuracyPercent ở trên; null khi không có CV chung nào
        Matrix = accuracy.Total == 0 ? null : PromptTestQueries.ToMatrix(accuracy),
        AccuracyLow = accuracy.Confidence95?.Low, AccuracyHigh = accuracy.Confidence95?.High,
        CreatedAt = Utc(info.CreatedAt), CompletedAt = info.CompletedAt is { } completed ? Utc(completed) : null
    };

    private static IQueryable<PromptCompareRunInfo> Project(IQueryable<EvaluationRun> runs) =>
        runs.Select(r => new PromptCompareRunInfo(
            r.RunId, r.RunType, r.Status, r.PromptVersionId, r.PromptVersion.VersionName,
            r.EvaluationSetId, r.EvaluationSet == null ? null : r.EvaluationSet.Name,
            r.ModelName, r.Temperature, r.KeywordWeight, r.AiWeight, r.PassThreshold,
            r.TotalItems, r.ProcessedItems, r.FailedItems, r.CreatedAt, r.CompletedAt));

    private static int ChangeRank(string change) => change switch
    {
        nameof(CompareChange.Fixed) => 0,
        nameof(CompareChange.Broken) => 1,
        nameof(CompareChange.Unscored) => 2,
        _ => 3
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
