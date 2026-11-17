using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Dashboard;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

public static class DashboardEndpoints
{
    public static WebApplication MapDashboardEndpoints(this WebApplication app)
    {
        // Same temporary HR boundary as A35/A40 until M6 supplies real authorization.
        if (!app.Environment.IsDevelopment() || !app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled)
            return app;
        app.MapGet("/api/dashboard/summary", SummaryAsync).WithTags("Dashboard").WithName("DashboardSummary")
            .WithSummary("Tổng quan tuyển dụng theo Job và ngày Việt Nam")
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            });
        return app;
    }

    private static async Task<Results<Ok<DashboardSummaryDto>, ProblemHttpResult>> SummaryAsync(
        HttpRequest request, SmartCvDbContext db, IOptions<ReviewFlagOptions> flags, CancellationToken ct)
    {
        // Parse strings ourselves so malformed/overflowing and repeated filters have Vietnamese ProblemDetails.
        foreach (var key in new[] { "jobId", "from", "to" })
            if (request.Query[key].Count > 1) return Problem(400, "Bộ lọc không hợp lệ", "Mỗi bộ lọc chỉ được có một giá trị.");
        var job = request.Query["jobId"].ToString();
        int? jobId = null;
        if (job.Length > 0)
        {
            if (!int.TryParse(job, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                return Problem(400, "Bộ lọc không hợp lệ", "Mã Job phải là số nguyên dương trong miền Int32.");
            jobId = parsed;
        }
        var (period, error) = DashboardPeriod.Parse(request.Query["from"], request.Query["to"]);
        if (period is null) return Problem(400, "Khoảng ngày không hợp lệ", error!);
        if (jobId is { } jid && !await db.Jobs.AsNoTracking().AnyAsync(j => j.JobId == jid, ct))
            return Problem(404, "Không tìm thấy Job", $"Không có Job #{jid}.");

        var apps = db.Applications.AsNoTracking().AsQueryable();
        if (jobId is not null) apps = apps.Where(a => a.JobId == jobId);
        if (period.FromUtc is { } start) apps = apps.Where(a => a.CreatedAt >= start);
        if (period.ToUtc is { } end) apps = apps.Where(a => a.CreatedAt < end);

        var cvs = db.Cvs.AsNoTracking().AsQueryable();
        if (jobId is not null) cvs = cvs.Where(c => apps.Select(a => a.CvId).Contains(c.CvId));
        else
        {
            if (period.FromUtc is { } first) cvs = cvs.Where(c => c.UploadedAt >= first);
            if (period.ToUtc is { } last) cvs = cvs.Where(c => c.UploadedAt < last);
        }
        var totalCvs = await cvs.CountAsync(ct);
        var failedCvs = await cvs.CountAsync(c => c.ExtractionStatus == ExtractionStatuses.Failed, ct);
        var current = await apps.GroupBy(a => a.Stage).Select(g => new { Stage = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Stage, g => g.Count, ct);
        var total = current.Values.Sum();
        var reached = new int[4];
        reached[0] = total;
        for (var i = 1; i < reached.Length; i++)
        {
            var beyond = FunnelCalculator.MainStages.Skip(i).ToArray();
            // Both ends matter for imported history that starts after New; EXISTS counts each app once.
            reached[i] = await apps.CountAsync(a => beyond.Contains(a.Stage)
                || a.Events.Any(e => beyond.Contains(e.FromStage) || beyond.Contains(e.ToStage)), ct);
        }
        var reasons = await apps.Where(a => a.Stage == ApplicationStages.Rejected && a.RejectReason != null)
            .GroupBy(a => a.RejectReason!).Select(g => new { Reason = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Reason, g => g.Count, ct);
        var flagged = await CountFlaggedAsync(db, apps, flags.Value, ct);
        return TypedResults.Ok(new DashboardSummaryDto
        {
            JobId = jobId, From = period.From, To = period.To,
            TotalCvs = totalCvs, FailedCvs = failedCvs, TotalApplications = total,
            ActiveJobs = await db.Jobs.AsNoTracking().CountAsync(j => j.IsActive && (jobId == null || j.JobId == jobId), ct),
            RunningRuns = await db.EvaluationRuns.AsNoTracking().CountAsync(r => r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running, ct),
            FlaggedUnresolved = flagged.Count, FlaggedJobId = flagged.TopJobId,
            Funnel = FunnelCalculator.Build(current, reached),
            RejectedCount = current.GetValueOrDefault(ApplicationStages.Rejected),
            OnHoldCount = current.GetValueOrDefault(ApplicationStages.OnHold),
            ScreeningTime = total == 0 ? new() : await ScreeningTimeAsync(db, jobId, period, ct),
            RejectReasons = RejectReasonCalculator.Build(reasons, current.GetValueOrDefault(ApplicationStages.Rejected)),
            RecentRuns = await RecentRunsAsync(db, jobId, ct)
        });
    }

    private static async Task<(int Count, int? TopJobId)> CountFlaggedAsync(SmartCvDbContext db,
        IQueryable<Core.Entities.Application> apps, ReviewFlagOptions options, CancellationToken ct)
    {
        var open = apps.Where(a => a.Stage == ApplicationStages.New || a.Stage == ApplicationStages.Reviewing);
        var screening = db.Evaluations.AsNoTracking().Where(e => e.Run.RunType == RunTypes.Screening);
        // Select latest BEFORE checking status. Pending/Failed supersedes an old flagged Completed result.
        var latest = screening.Where(e => open.Any(a => a.ApplicationId == e.ApplicationId)
            && !screening.Any(n => n.ApplicationId == e.ApplicationId && (n.CreatedAt > e.CreatedAt
                || (n.CreatedAt == e.CreatedAt && n.EvaluationId > e.EvaluationId))))
            .Where(e => e.Status == EvaluationStatuses.Completed);
        var counts = new Dictionary<int, int>();
        var after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Bounded scalar batches. Reasoning is required ONLY for A40's saved cap audit marker.
            var rows = await latest.Where(e => e.EvaluationId > after).OrderBy(e => e.EvaluationId).Take(256)
                .Select(e => new
                {
                    e.EvaluationId, e.Application.JobId, e.Status, e.KeywordScore, e.AiScore, e.FinalScore,
                    e.ScoreSource, e.Reasoning, e.Run.KeywordWeight, e.Run.AiWeight,
                    TextLength = e.Application.Cv.ExtractedText == null ? (int?)null : (e.Application.Cv.ExtractedText + "#").Length - 1,
                    MissingMustHave = e.Skills.Any(s => !s.IsMatched && s.JobSkill.SkillType == SkillTypes.MustHave)
                }).ToListAsync(ct);
            foreach (var row in rows)
                if (ReviewFlags.Evaluate(new(row.Status, row.KeywordScore, row.AiScore, row.FinalScore, row.ScoreSource,
                    row.TextLength, row.KeywordWeight, row.AiWeight, row.MissingMustHave, row.Reasoning), options).Count > 0)
                    counts[row.JobId] = counts.GetValueOrDefault(row.JobId) + 1;
            if (rows.Count < 256) break;
            after = rows[^1].EvaluationId;
        }
        return (counts.Values.Sum(), counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Select(k => (int?)k.Key).FirstOrDefault());
    }

    private sealed class TimingAggregate
    {
        public int Count { get; set; }
        public decimal? AverageHours { get; set; }
        public decimal? MedianHours { get; set; }
    }

    private static async Task<ScreeningTimeDto> ScreeningTimeAsync(SmartCvDbContext db, int? jobId, DashboardPeriod period, CancellationToken ct)
    {
        // SQL Server computes the exact median, returning ONE row instead of every application's timing.
        // DATEDIFF_BIG avoids the 68-year overflow of DATEDIFF(SECOND).
        var rows = await db.Database.SqlQuery<TimingAggregate>($"""
            SELECT COUNT(*) AS [Count], CAST(AVG(Hours) AS decimal(28,10)) AS AverageHours,
                   CAST(MAX(Median) AS decimal(28,10)) AS MedianHours
            FROM (
                SELECT Hours, PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY Hours) OVER () AS Median
                FROM (
                    SELECT CASE WHEN decision.FirstAt < a.CreatedAt THEN CAST(0 AS decimal(28,10))
                           ELSE CAST(DATEDIFF_BIG(MICROSECOND, a.CreatedAt, decision.FirstAt) AS decimal(28,4)) / 3600000000 END AS Hours
                    FROM Applications a
                    JOIN (SELECT ApplicationId, MIN(CreatedAt) AS FirstAt FROM ApplicationEvents GROUP BY ApplicationId) decision
                      ON decision.ApplicationId = a.ApplicationId
                    WHERE ({jobId} IS NULL OR a.JobId = {jobId})
                      AND ({period.FromUtc} IS NULL OR a.CreatedAt >= {period.FromUtc})
                      AND ({period.ToUtc} IS NULL OR a.CreatedAt < {period.ToUtc})
                ) durations
            ) timings
            """).ToListAsync(ct);
        var result = rows.Single();
        return ScreeningTimeCalculator.FromAggregate(result.Count, result.AverageHours, result.MedianHours);
    }

    private static async Task<List<RunSummaryDto>> RecentRunsAsync(SmartCvDbContext db, int? jobId, CancellationToken ct)
    {
        var query = db.EvaluationRuns.AsNoTracking();
        if (jobId is not null) query = query.Where(r => r.Evaluations.Any(e => e.Application.JobId == jobId));
        var runs = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RunId).Take(5)
            .Select(r => new RunSummaryDto
            {
                RunId = r.RunId, RunType = r.RunType, Status = r.Status, PromptVersionName = r.PromptVersion.VersionName,
                ModelName = r.ModelName, TotalItems = r.TotalItems, ProcessedItems = r.ProcessedItems,
                FailedItems = r.FailedItems, AccuracyPercent = r.AccuracyPercent, CreatedAt = r.CreatedAt, CompletedAt = r.CompletedAt
            }).ToListAsync(ct);
        if (runs.Count == 0) return runs;
        var ids = runs.Select(r => r.RunId).ToArray();
        var jobs = await db.Evaluations.AsNoTracking().Where(e => ids.Contains(e.RunId))
            .GroupBy(e => new { e.RunId, e.Application.JobId, e.Application.Job.Title })
            .Select(g => new { g.Key.RunId, g.Key.JobId, g.Key.Title, Count = g.Count() }).ToListAsync(ct);
        return runs.Select(r => new RunSummaryDto
        {
            RunId = r.RunId, RunType = r.RunType, Status = r.Status, PromptVersionName = r.PromptVersionName,
            ModelName = r.ModelName, TotalItems = r.TotalItems, ProcessedItems = r.ProcessedItems,
            FailedItems = r.FailedItems, AccuracyPercent = r.AccuracyPercent,
            CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
            CompletedAt = r.CompletedAt is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
            Jobs = jobs.Where(j => j.RunId == r.RunId).OrderBy(j => j.JobId)
                .Select(j => new JobCountDto { JobId = j.JobId, Title = j.Title, Count = j.Count }).ToList()
        }).ToList();
    }

    private static ProblemHttpResult Problem(int status, string title, string detail) => TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
