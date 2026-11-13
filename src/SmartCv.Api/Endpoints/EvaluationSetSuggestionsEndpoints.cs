using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Evaluation;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

public static partial class EvaluationSetEndpoints
{
    private static async Task<Results<Ok<LabelSuggestionResponse>, ProblemHttpResult>> SuggestionsAsync(
        int id, SmartCvDbContext db, CancellationToken ct, int? jobId = null)
    {
        if (!await db.EvaluationSets.AsNoTracking().AnyAsync(s => s.EvaluationSetId == id, ct)) return NotFoundSet(id);

        // Same candidate pool as the A36 guide: no decision yet / failed extraction is excluded.
        // The set scopes the CURRENT label, not the candidate pool (which includes unlabeled applications).
        var candidates = db.Applications.AsNoTracking()
            .Where(a => a.Stage != ApplicationStages.New && a.Cv.ExtractionStatus != ExtractionStatuses.Failed && a.Cv.AnonymizedAt == null);
        if (jobId is { } job) candidates = candidates.Where(a => a.JobId == job);
        var rows = await (from app in candidates
            join label in db.HrLabels.AsNoTracking().Where(h => h.EvaluationSetId == id)
                on app.ApplicationId equals label.ApplicationId into labels
            from label in labels.DefaultIfEmpty()
            select new
            {
                app.ApplicationId, app.JobId, JobTitle = app.Job.Title,
                CandidateName = app.Cv.Candidate != null ? app.Cv.Candidate.FullName : null,
                FileName = app.Cv.OriginalFileName,
                app.Stage, app.RejectReason, app.StageUpdatedAt,
                CurrentLabel = label == null ? (bool?)null : label.IsPass,
                CurrentNote = label == null ? null : label.Note,
                CurrentLabeledBy = label == null ? null : label.LabeledBy
            }).ToListAsync(ct);

        var items = new List<LabelSuggestionDto>();
        var skipped = 0;
        foreach (var row in rows)
        {
            if (LabelSuggestionRules.Suggest(row.Stage, row.RejectReason) is not { } suggested) { skipped++; continue; }
            var status = LabelSuggestionRules.Compare(suggested, row.CurrentLabel);
            var reason = LabelSuggestionRules.Explain(row.Stage, row.RejectReason);
            if (status == LabelSuggestionStatus.Conflict)
                reason += $". Đề xuất {(suggested ? "Đạt" : "Không đạt")}, khác nhãn hiện tại {(row.CurrentLabel == true ? "Đạt" : "Không đạt")}. HR cần kiểm tra trước khi lưu.";
            items.Add(new()
            {
                ApplicationId = row.ApplicationId, CandidateName = row.CandidateName, FileName = row.FileName,
                JobId = row.JobId, JobTitle = row.JobTitle, Stage = row.Stage, RejectReason = row.RejectReason,
                StageUpdatedAt = row.StageUpdatedAt is { } updated ? Utc(updated) : null,
                SuggestedIsPass = suggested, CurrentLabel = row.CurrentLabel, CurrentNote = row.CurrentNote,
                CurrentLabeledBy = row.CurrentLabeledBy, Reason = reason, Status = status.ToString()
            });
        }

        return TypedResults.Ok(new LabelSuggestionResponse
        {
            EvaluationSetId = id, JobId = jobId, SkippedCount = skipped,
            NewCount = items.Count(i => i.Status == nameof(LabelSuggestionStatus.New)),
            SameCount = items.Count(i => i.Status == nameof(LabelSuggestionStatus.Same)),
            ConflictCount = items.Count(i => i.Status == nameof(LabelSuggestionStatus.Conflict)),
            Items = items.OrderBy(i => i.Status switch
                { nameof(LabelSuggestionStatus.Conflict) => 0, nameof(LabelSuggestionStatus.New) => 1, _ => 2 })
                .ThenBy(i => i.JobTitle, StringComparer.Ordinal).ThenBy(i => i.ApplicationId).ToList()
        });
    }
}
