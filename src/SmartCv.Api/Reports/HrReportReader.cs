using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Reports;

/// <summary>
/// Workspace-scoped EF queries only: no IgnoreQueryFilters / BeginSystem / caller-controlled workspaceId.
/// Bounded aggregation rejects oversized requests rather than silently computing partial statistics.
/// </summary>
public static class HrReportReader
{
    public static async Task<(List<HrReportFact>? Rows, int Status, string? Error)> ReadAsync(
        SmartCvDbContext db, HrReportFilter filter, CancellationToken ct)
    {
        if (filter.JobId is int job && !await db.Jobs.AsNoTracking().AnyAsync(j => j.JobId == job, ct))
            return (null, 404, "Không tìm thấy Job trong workspace hiện hành.");
        if (filter.RunId is int run && !await db.EvaluationRuns.AsNoTracking()
                .AnyAsync(r => r.RunId == run && r.RunType == RunTypes.Screening, ct))
            return (null, 404, "Không tìm thấy lượt Screening trong workspace hiện hành.");

        var apps = db.Applications.AsNoTracking().AsQueryable();
        if (filter.JobId is int id) apps = apps.Where(a => a.JobId == id);
        if (filter.Period.FromUtc is DateTime from) apps = apps.Where(a => a.CreatedAt >= from);
        if (filter.Period.ToUtc is DateTime to) apps = apps.Where(a => a.CreatedAt < to);
        if (filter.Stage != "all") apps = apps.Where(a => a.Stage == filter.Stage);
        if (filter.RunId is int runId)
            apps = apps.Where(a => a.Evaluations.Any(e => e.RunId == runId && e.Run.RunType == RunTypes.Screening));

        if (await apps.CountAsync(ct) > HrReportFilter.MaxApplicationsToAggregate)
            return (null, 413, $"Phạm vi trên {HrReportFilter.MaxApplicationsToAggregate:N0} lượt ứng tuyển. Chọn Job hoặc rút ngắn ngày để tránh tải toàn bộ dữ liệu.");

        // Only necessary scalar fields. Applicant PII is fetched only on an explicitly non-anonymous request.
        var facts = await apps.OrderBy(a => a.ApplicationId)
            .Select(a => new HrApplicationFact(
                a.ApplicationId, a.CvId, a.Cv.CandidateId,
                filter.Anonymous || a.Cv.AnonymizedAt != null || a.Cv.Candidate == null
                    ? null : a.Cv.Candidate.FullName,
                a.JobId, a.Job.Title, a.CreatedAt, a.Stage, a.RejectReason,
                a.Events.OrderBy(e => e.CreatedAt).Select(e => (DateTime?)e.CreatedAt).FirstOrDefault(),
                a.Stage == ApplicationStages.Shortlisted || a.Stage == ApplicationStages.Interview ||
                a.Events.Any(e => e.ToStage == ApplicationStages.Shortlisted || e.ToStage == ApplicationStages.Interview ||
                                  e.FromStage == ApplicationStages.Shortlisted || e.FromStage == ApplicationStages.Interview),
                a.Flags.Any(f => f.Status == ManualFlagStatuses.Open)))
            .ToListAsync(ct);

        // Select latest Screening ONLY (PromptTest is not a hiring decision). SQL Server translates GroupBy + First to row-number.
        var appIds = apps.Select(a => a.ApplicationId);
        var latest = await db.Evaluations.AsNoTracking()
            .Where(e => appIds.Contains(e.ApplicationId) && e.Run.RunType == RunTypes.Screening &&
                        (filter.RunId == null || e.RunId == filter.RunId))
            .GroupBy(e => e.ApplicationId)
            .Select(g => g.OrderByDescending(e => e.Run.CreatedAt).ThenByDescending(e => e.EvaluationId)
                .Select(e => new HrScoreFact(e.ApplicationId, e.RunId, e.Status, e.ScoreSource,
                    e.FinalScore, e.Run.PassThreshold, e.FitLabel)).First())
            .ToListAsync(ct);
        var byApp = latest.ToDictionary(e => e.ApplicationId);
        var result = facts.Select(a => new HrReportFact(a, byApp.GetValueOrDefault(a.ApplicationId)))
            .Where(a => HrReportRollup.Include(a, filter.Evaluation)).ToList();
        return (result, 200, null);
    }
}
