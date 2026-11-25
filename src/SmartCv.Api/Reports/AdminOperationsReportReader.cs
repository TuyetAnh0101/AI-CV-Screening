using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Contracts;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Reports;

/// <summary>
/// Admin-only aggregation. This reader MUST ONLY be called by the SystemAdmin endpoint group.
/// Explicit IgnoreQueryFilters is intentional for global aggregates; projections never select
/// candidate/CV content, prompts, emails, scores, access tokens or external provider keys.
/// </summary>
public static class AdminOperationsReportReader
{
    private static readonly string[] ExportActions =
    [
        "runs.ranking_exported", "hr.recruitment_report_exported",
        "hr.recruitment_report_pdf_exported", "candidates.export_access_granted",
        "admin.operations_report_exported"
    ];

    public static async Task<AdminReportSummaryDto> SummaryAsync(
        SmartCvDbContext db, AdminReportFilter filter, DateTime nowUtc, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().GroupBy(_ => 1).Select(g => new
        {
            Total = g.LongCount(), Active = g.LongCount(x => x.IsActive),
            Created = g.LongCount(x => x.CreatedAt >= filter.FromUtc && x.CreatedAt < filter.UntilUtc)
        }).SingleOrDefaultAsync(ct);
        var signup = await db.Users.AsNoTracking().GroupBy(x => x.SignupSource)
            .Select(g => new { Code = g.Key, Count = g.LongCount() }).ToListAsync(ct);
        // Avoid reflecting unknown/hostile source strings into CSV or JSON dimension labels.
        var signupGroups = signup.GroupBy(x => x.Code switch
        {
            "Admin" => "Admin", "Local" => "Local", "google" => "google",
            "linkedin" => "linkedin", _ => "Other"
        }).Select(g => new AdminReportDimensionDto(g.Key, g.Sum(x => x.Count)))
          .OrderBy(x => x.Code, StringComparer.Ordinal).ToArray();

        var w = await db.Workspaces.AsNoTracking().GroupBy(_ => 1).Select(g => new
        {
            Total = g.LongCount(), Active = g.LongCount(x => x.IsActive),
            Personal = g.LongCount(x => x.Kind == "Personal"),
            Internal = g.LongCount(x => x.Kind == "Internal"),
            AiLimited = g.LongCount(x => x.Kind == "Personal" && x.DailyAiEvaluationLimit != null),
            CvLimited = g.LongCount(x => x.Kind == "Personal" && x.MaxCvCount != null)
        }).SingleOrDefaultAsync(ct);
        var kinds = await db.Workspaces.AsNoTracking().GroupBy(x => x.Kind)
            .Select(g => new { Code = g.Key, Count = g.LongCount() }).ToListAsync(ct);
        var kindGroups = kinds.GroupBy(x => x.Code switch
        {
            "Personal" => "Personal", "Internal" => "Internal", _ => "Other"
        }).Select(g => new AdminReportDimensionDto(g.Key, g.Sum(x => x.Count)))
          .OrderBy(x => x.Code, StringComparer.Ordinal).ToArray();

        var ledgers = await db.AiUsageEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.CreatedAtUtc >= filter.FromUtc && x.CreatedAtUtc < filter.UntilUtc)
            .GroupBy(x => x.Source)
            .Select(g => new { Source = g.Key, Units = g.Sum(x => (long)(x.Units - x.UnitsReleased)) })
            .ToListAsync(ct);
        long Units(string source) => ledgers.Where(x => x.Source == source).Sum(x => x.Units);
        var failedRuns = await db.EvaluationRuns.IgnoreQueryFilters().AsNoTracking()
            .LongCountAsync(x => x.CreatedAt >= filter.FromUtc && x.CreatedAt < filter.UntilUtc
                && x.Status == "Failed", ct);
        // Evaluation status is process outcome, NOT a confirmed upstream provider failure.
        var failedEvals = await db.Evaluations.IgnoreQueryFilters().AsNoTracking()
            .LongCountAsync(x => x.Run.CreatedAt >= filter.FromUtc &&
                x.Run.CreatedAt < filter.UntilUtc && x.Status == "Failed", ct);

        var audits = db.AuditEvents.AsNoTracking()
            .Where(x => x.OccurredAtUtc >= filter.FromUtc && x.OccurredAtUtc < filter.UntilUtc);
        var s = await audits.GroupBy(_ => 1).Select(g => new
        {
            LoginFailed = g.LongCount(x => x.ActionCode == "auth.login" &&
                (x.Outcome == "Failed" || x.Outcome == "Denied")),
            ExternalRejected = g.LongCount(x => x.ActionCode == "auth.external_rejected"),
            Denied = g.LongCount(x => x.Outcome == "Denied"),
            DeniedAccess = g.LongCount(x => x.Outcome == "Denied" &&
                (x.FailureCode == "access_denied" || x.FailureCode == "http_403")),
            AdminActions = g.LongCount(x => x.ActorRoleSnapshot == "Admin" &&
                x.Category != "Authentication" && x.Category != "Session"),
            HrReports = g.LongCount(x =>
                (x.ActionCode == "hr.recruitment_report_exported" ||
                 x.ActionCode == "hr.recruitment_report_pdf_exported") && x.Outcome == "AccessGranted")
        }).SingleOrDefaultAsync(ct);

        var exports = await db.ExportLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.AuditEvent.OccurredAtUtc >= filter.FromUtc &&
                        x.AuditEvent.OccurredAtUtc < filter.UntilUtc &&
                        x.AuditEvent.Outcome == "AccessGranted")
            .GroupBy(_ => 1).Select(g => new { Files = g.LongCount(), Rows = g.Sum(x => (long)x.RowCount) })
            .SingleOrDefaultAsync(ct);

        var provider = await ReadProviderAttemptsAsync(db, filter, ct);
        return new AdminReportSummaryDto(filter.ToDto(nowUtc),
            new(u?.Total ?? 0, u?.Active ?? 0, (u?.Total ?? 0) - (u?.Active ?? 0),
                u?.Created ?? 0, signupGroups),
            new(w?.Total ?? 0, w?.Active ?? 0, (w?.Total ?? 0) - (w?.Active ?? 0),
                w?.Personal ?? 0, w?.Internal ?? 0, w?.AiLimited ?? 0, w?.CvLimited ?? 0, kindGroups),
            new(ledgers.Sum(x => x.Units), Units("Screening"), Units("PromptTest"), Units("Preview"),
                null, "not_instrumented: quota rejection HTTP 429 is not reliably recorded as a dedicated event",
                failedRuns, failedEvals, provider.Available,
                provider.Attempts, provider.Ok, provider.Errors, provider.Cancelled, provider.Status),
            new(s?.LoginFailed ?? 0, s?.ExternalRejected ?? 0, s?.Denied ?? 0,
                s?.DeniedAccess ?? 0, s?.AdminActions ?? 0),
            new(exports?.Files ?? 0, s?.HrReports ?? 0, exports?.Rows ?? 0,
                "Ranking: ExportLogs.RowCount. Báo cáo HR tổng hợp: chỉ có AuditEvents, không có RowCount cấu trúc."),
            new("not_available", null, "Chưa có nguồn xác nhận backup A62; không suy đoán trạng thái sao lưu."));
    }

    public static async Task<AdminReportTrendDto> TrendAsync(
        SmartCvDbContext db, AdminReportFilter filter, DateTime nowUtc, CancellationToken ct)
    {
        // SQL Server translates AddHours to DATEADD and Date to CONVERT(date,...).
        // Grouping happens on DB; no raw business records are transferred to Admin API.
        var usage = await db.AiUsageEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.CreatedAtUtc >= filter.FromUtc && x.CreatedAtUtc < filter.UntilUtc)
            .GroupBy(x => x.CreatedAtUtc.AddHours(7).Date)
            .Select(g => new
            {
                Date = g.Key,
                Screening = g.Sum(x => x.Source == "Screening" ? (long)(x.Units - x.UnitsReleased) : 0L),
                PromptTest = g.Sum(x => x.Source == "PromptTest" ? (long)(x.Units - x.UnitsReleased) : 0L),
                Preview = g.Sum(x => x.Source == "Preview" ? (long)(x.Units - x.UnitsReleased) : 0L)
            }).ToListAsync(ct);
        var runs = await db.EvaluationRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.CreatedAt >= filter.FromUtc && x.CreatedAt < filter.UntilUtc && x.Status == "Failed")
            .GroupBy(x => x.CreatedAt.AddHours(7).Date)
            .Select(g => new { Date = g.Key, Count = g.LongCount() }).ToListAsync(ct);
        var auditTrend = await db.AuditEvents.AsNoTracking()
            .Where(x => x.OccurredAtUtc >= filter.FromUtc && x.OccurredAtUtc < filter.UntilUtc)
            .GroupBy(x => x.OccurredAtUtc.AddHours(7).Date)
            .Select(g => new { Date = g.Key,
                Denied = g.LongCount(x => x.Outcome == "Denied") }).ToListAsync(ct);

        var a = usage.ToDictionary(x => DateOnly.FromDateTime(x.Date));
        var r = runs.ToDictionary(x => DateOnly.FromDateTime(x.Date));
        var d = auditTrend.ToDictionary(x => DateOnly.FromDateTime(x.Date));
        var points = Enumerable.Range(0, filter.Days).Select(i =>
        {
            var date = filter.From.AddDays(i);
            var u = a.GetValueOrDefault(date);
            return new AdminReportDayDto(date, u?.Screening ?? 0, u?.PromptTest ?? 0,
                u?.Preview ?? 0, r.GetValueOrDefault(date)?.Count ?? 0,
                d.GetValueOrDefault(date)?.Denied ?? 0, null);
        }).ToArray();
        return new(filter.ToDto(nowUtc), points,
            "AiUsageEntries.Units - UnitsReleased (giữ quota, không phải token)",
            "EvaluationRuns.Status = Failed, theo CreatedAt", "AuditEvents.Outcome = Denied");
    }

    public static async Task<AdminReportExportPageDto> ExportsAsync(
        SmartCvDbContext db, AdminReportFilter filter, DateTime nowUtc,
        CancellationToken ct, bool allForDownload = false)
    {
        var baseQuery = db.AuditEvents.AsNoTracking().Where(x =>
            x.OccurredAtUtc >= filter.FromUtc && x.OccurredAtUtc < filter.UntilUtc &&
            x.Outcome == "AccessGranted" && ExportActions.Contains(x.ActionCode));
        var total = await baseQuery.LongCountAsync(ct);
        var query = baseQuery.OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.AuditEventId);
        var events = allForDownload
            ? await query.Take(MaxDownloadRowsPlusOne()).ToListAsync(ct)
            : await query.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToListAsync(ct);
        if (allForDownload && (total > AdminReportFilter.MaxDownloadEvents || events.Count > AdminReportFilter.MaxDownloadEvents))
            return new(filter.ToDto(nowUtc), filter.Page, filter.PageSize, total, []);

        var ids = events.Select(x => x.AuditEventId).ToArray();
        var logs = await db.ExportLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => ids.Contains(x.AuditEventId))
            .Select(x => new { x.AuditEventId, x.RowCount, x.Anonymized, x.Format, x.WorkspaceId })
            .ToDictionaryAsync(x => x.AuditEventId, ct);
        var items = events.Select(e =>
        {
            var exists = logs.TryGetValue(e.AuditEventId, out var log);
            var format = exists ? log!.Format : e.ActionCode switch
            {
                "hr.recruitment_report_pdf_exported" => "pdf",
                "hr.recruitment_report_exported" => "xlsx",
                "admin.operations_report_exported" => "csv",
                "candidates.export_access_granted" => "candidate-data",
                _ => "unknown"
            };
            return new AdminReportExportEventDto(e.AuditEventId,
                DateTime.SpecifyKind(e.OccurredAtUtc, DateTimeKind.Utc),
                e.ActorUserId, e.ActorRoleSnapshot,
                e.WorkspaceId ?? (exists ? (int?)log!.WorkspaceId : null),
                e.ActionCode, format, exists ? log!.RowCount : null,
                exists ? log!.Anonymized : null,
                exists ? "ExportLogs" : "not_recorded_as_structured_count");
        }).ToArray();
        return new(filter.ToDto(nowUtc), filter.Page, filter.PageSize, total, items);
    }

    public static async Task<long> ExportCountAsync(SmartCvDbContext db, AdminReportFilter filter, CancellationToken ct) =>
        await db.AuditEvents.AsNoTracking().LongCountAsync(x =>
            x.OccurredAtUtc >= filter.FromUtc && x.OccurredAtUtc < filter.UntilUtc &&
            x.Outcome == "AccessGranted" && ExportActions.Contains(x.ActionCode), ct);

    private static int MaxDownloadRowsPlusOne() => AdminReportFilter.MaxDownloadEvents + 1;

    private sealed record ProviderCounts(bool Available, long? Attempts, long? Ok,
        long? Errors, long? Cancelled, string Status);

    private static async Task<ProviderCounts> ReadProviderAttemptsAsync(
        SmartCvDbContext db, AdminReportFilter filter, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
            return new(false, null, null, null, null, "not_available_non_sql_provider");
        try
        {
            var connectionString = db.Database.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
                return new(false, null, null, null, null, "not_configured");
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            const string sql = """
                SELECT COUNT_BIG(*),
                       COUNT_BIG(CASE WHEN Outcome = N'HttpOk' THEN 1 END),
                       COUNT_BIG(CASE WHEN Outcome IN (N'HttpError',N'NetworkError',N'Timeout') THEN 1 END),
                       COUNT_BIG(CASE WHEN Outcome = N'Cancelled' THEN 1 END)
                FROM dbo.GeminiInvocationLogs
                WHERE OccurredAtUtc >= @from AND OccurredAtUtc < @until
                """;
            await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 12 };
            cmd.Parameters.Add("@from", SqlDbType.DateTime2).Value = filter.FromUtc;
            cmd.Parameters.Add("@until", SqlDbType.DateTime2).Value = filter.UntilUtc;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return new(false, null, null, null, null, "no_aggregate_result");
            return new(true, r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3),
                "GeminiInvocationLogs attempts (includes retries, not distinct CVs)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SqlException)
        {
            // Missing telemetry table or service outage must not look like a real zero.
            return new(false, null, null, null, null, "telemetry_unavailable_or_schema_missing");
        }
        catch (InvalidOperationException)
        {
            return new(false, null, null, null, null, "telemetry_unavailable");
        }
    }
}
