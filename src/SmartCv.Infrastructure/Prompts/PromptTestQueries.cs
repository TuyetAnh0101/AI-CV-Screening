using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Prompts;

/// <summary>A15 read projections only; no accuracy calculation or prompt comparison.</summary>
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
                AccuracyPercent = r.AccuracyPercent
            });
    }

    public static async Task<PromptTestProgressDto?> LoadProgressAsync(this SmartCvDbContext db, int runId, CancellationToken ct)
    {
        var run = await db.QueryRuns().SingleOrDefaultAsync(r => r.RunId == runId, ct);
        if (run is null) return null;
        var items = await db.Evaluations.AsNoTracking().Where(e => e.RunId == runId)
            .OrderBy(e => e.EvaluationId)
            .Select(e => new PromptTestEvaluationDto
            {
                EvaluationId = e.EvaluationId, ApplicationId = e.ApplicationId, CvId = e.Application.CvId,
                CandidateName = e.Application.Cv.Candidate == null ? null : e.Application.Cv.Candidate.FullName,
                FileName = e.Application.Cv.OriginalFileName, JobId = e.Application.JobId, JobTitle = e.Application.Job.Title,
                HrIsPass = e.Application.HrLabels.Where(h => h.EvaluationSetId == run.EvaluationSetId)
                    .Select(h => (bool?)h.IsPass).FirstOrDefault(),
                HrNote = e.Application.HrLabels.Where(h => h.EvaluationSetId == run.EvaluationSetId)
                    .Select(h => h.Note).FirstOrDefault(),
                Status = e.Status, ExtractionStatus = e.Application.Cv.ExtractionStatus,
                KeywordScore = e.KeywordScore, AiScore = e.AiScore, FinalScore = e.FinalScore,
                PredictedPass = e.PredictedPass, ScoreSource = e.ScoreSource, FitLabel = e.FitLabel,
                Reasoning = e.Reasoning, ErrorMessage = e.ErrorMessage
            }).ToListAsync(ct);
        return new PromptTestProgressDto
        {
            RunId = run.RunId, PromptVersionId = run.PromptVersionId, PromptVersionName = run.PromptVersionName,
            EvaluationSetId = run.EvaluationSetId, EvaluationSetName = run.EvaluationSetName,
            ModelName = run.ModelName, Temperature = run.Temperature, KeywordWeight = run.KeywordWeight,
            AiWeight = run.AiWeight, PassThreshold = run.PassThreshold, Status = run.Status,
            TotalItems = run.TotalItems, ProcessedItems = run.ProcessedItems, FailedItems = run.FailedItems,
            CreatedAt = run.CreatedAt, CompletedAt = run.CompletedAt, AccuracyPercent = run.AccuracyPercent,
            Items = items
        };
    }
}
