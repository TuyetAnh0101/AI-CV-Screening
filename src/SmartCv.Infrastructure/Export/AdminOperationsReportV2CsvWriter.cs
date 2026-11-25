using System.Globalization;
using System.Text;
using SmartCv.Core.Contracts;

namespace SmartCv.Infrastructure.Export;

/// <summary>One consistent machine-readable CSV schema, strict report selection, no personal rows.</summary>
public static class AdminOperationsReportV2CsvWriter
{
    public const string ContentType = "text/csv; charset=utf-8";
    public static string FileName(DateTime utc, string report) =>
        $"scapp-admin-{report}-{utc.AddHours(7):yyyyMMdd-HHmmss}.csv";

    public static byte[] Write(AdminReportSummaryDto summary, AdminReportSummaryDto previous,
        AdminReportTrendDto trend, IReadOnlyList<AdminReportExportEventDto> journal,
        AdminReportHealthDto? health, string report)
    {
        if (!AdminReportV2Kinds.Allowed.Contains(report, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported report selection.", nameof(report));
        var sb = new StringBuilder();
        Row("section", "date_vn", "metric", "dimension", "value", "source", "note");
        Row("meta", "", "report", "", report, "request", "All dates inclusive UTC+7");
        Row("meta", "", "from", "", summary.Period.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "period", "");
        Row("meta", "", "to", "", summary.Period.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "period", "");
        Row("meta", "", "captured_at_utc", "", summary.Period.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture), "TimeProvider", "Multiple queries; not a DB atomic snapshot");
        void Metric(string section, string metric, long? n, string source, string note = "", string dimension = "") =>
            Row(section, "", metric, dimension, n?.ToString(CultureInfo.InvariantCulture) ?? "not_available", source, note);
        void Status(string section, string metric, string? value, string source, string note = "") =>
            Row(section, "", metric, "", value ?? "not_available", source, note);
        bool Has(string section) => report is AdminReportV2Kinds.All || report == section;

        if (Has(AdminReportV2Kinds.Accounts))
        {
            Metric("accounts", "total_now", summary.Accounts.TotalNow, "Users", "snapshot, not within period");
            Metric("accounts", "enabled_now", summary.Accounts.EnabledNow, "Users.IsActive", "not online");
            Metric("accounts", "locked_now", summary.Accounts.LockedNow, "Users.IsActive");
            Metric("accounts", "created_in_period", summary.Accounts.CreatedInPeriod, "Users.CreatedAt");
            Metric("accounts", "created_previous_period", previous.Accounts.CreatedInPeriod, "Users.CreatedAt");
            foreach (var x in summary.Accounts.BySignupSource)
                Metric("accounts", "signup_source", x.Count, "Users.SignupSource", "current snapshot", x.Code);
        }
        if (Has(AdminReportV2Kinds.Workspaces))
        {
            Metric("workspaces", "total_now", summary.Workspaces.TotalNow, "Workspaces", "current snapshot");
            Metric("workspaces", "active_now", summary.Workspaces.ActiveNow, "Workspaces.IsActive");
            Metric("workspaces", "inactive_now", summary.Workspaces.InactiveNow, "Workspaces.IsActive");
            Metric("workspaces", "personal_with_daily_ai_limit", summary.Workspaces.PersonalWithDailyAiLimitNow, "DailyAiEvaluationLimit");
            Metric("workspaces", "personal_with_cv_limit", summary.Workspaces.PersonalWithCvLimitNow, "MaxCvCount");
            foreach (var x in summary.Workspaces.ByKind)
                Metric("workspaces", "kind", x.Count, "Workspaces.Kind", "current snapshot", x.Code);
        }
        if (Has(AdminReportV2Kinds.Ai))
        {
            Metric("ai", "net_reserved_units", summary.Ai.NetReservedUnits, "AiUsageEntries", "not Gemini tokens");
            Metric("ai", "screening_units", summary.Ai.ScreeningUnits, "AiUsageEntries");
            Metric("ai", "prompt_test_units", summary.Ai.PromptTestUnits, "AiUsageEntries");
            Metric("ai", "preview_units", summary.Ai.PreviewUnits, "AiUsageEntries");
            Metric("ai", "failed_runs", summary.Ai.FailedRuns, "EvaluationRuns");
            Metric("ai", "failed_evaluations", summary.Ai.FailedEvaluations, "Evaluations");
            Metric("ai", "gemini_attempts", summary.Ai.GeminiAttempts, summary.Ai.ProviderSourceStatus, "includes retries");
            Metric("ai", "gemini_errors_or_timeout", summary.Ai.GeminiErrorsOrTimeouts, "GeminiInvocationLogs");
            Metric("ai", "quota_rejection_events", summary.Ai.QuotaExceededEvents, summary.Ai.QuotaExceededSourceStatus, "not instrumented; do not infer zero");
            foreach (var d in trend.Points)
                Row("ai_daily", d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "net_reserved_units", "",
                    d.NetReservedUnits.ToString(CultureInfo.InvariantCulture), "AiUsageEntries", "UTC+7 day");
        }
        if (Has(AdminReportV2Kinds.Security))
        {
            Metric("security", "failed_password_logins", summary.Security.FailedPasswordLogins, "AuditEvents");
            Metric("security", "rejected_external", summary.Security.RejectedExternalCallbacks, "AuditEvents");
            Metric("security", "denied_audit_events", summary.Security.DeniedAuditEvents, "AuditEvents", "not every HTTP 403");
            Metric("security", "access_denied", summary.Security.AccessDeniedAuditEvents, "AuditEvents");
            Metric("security", "admin_actions", summary.Security.AdministrativeAuditEvents, "AuditEvents");
            foreach (var d in trend.Points)
                Row("security_daily", d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "denied_audit_events", "",
                    d.DeniedAuditEvents.ToString(CultureInfo.InvariantCulture), "AuditEvents", "UTC+7 day");
        }
        if (Has(AdminReportV2Kinds.Operations))
        {
            Status("operations", "readiness", health?.ReadinessStatus, "ApplicationReadiness", "instantaneous; not uptime");
            if (health is { } h)
            {
                foreach (var c in h.Checks)
                    Status("operations", "health_check", c.Ok ? "ok" : "failed", "ApplicationReadiness", c.Name + ": " + c.Message);
                foreach (var w in h.WorkerConfiguration)
                    Status("operations", "worker_config", w.Enabled ? "enabled" : "disabled", "configuration", w.Name + ": " + w.Meaning);
                Status("operations", "email_scope", h.EmailCheckScope, "EmailOptions", "not SMTP probe");
                Status("operations", "uptime", h.ApiUptimeStatus, "not_monitored", "not an uptime metric");
                Status("operations", "backup", h.BackupStatus, "A62", "not verified");
            }
        }
        if (Has(AdminReportV2Kinds.Exports))
        {
            Metric("exports", "ranking_files", summary.Exports.RankingFiles, "ExportLogs");
            Metric("exports", "ranking_rows", summary.Exports.RankingRows, "ExportLogs.RowCount");
            Metric("exports", "hr_aggregate_files", summary.Exports.HrAggregateFiles, "AuditEvents", "row count unavailable");
            foreach (var e in journal)
                Row("export_event", e.OccurredAtUtc.AddHours(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    e.Category, e.Format,
                    e.RowCount?.ToString(CultureInfo.InvariantCulture) ?? "not_recorded",
                    $"AuditEvents#{e.AuditEventId}",
                    $"actor_id={e.ActorUserId};role={e.ActorRole};workspace_id={e.WorkspaceId};anonymized={e.Anonymous};rows_source={e.RowCountSource}");
        }
        foreach (var s in summary.Sources)
            Row("source", "", s.Metric, "", s.Source, "SCApp", s.Basis);
        Row("source", "", "comparison", "", "current vs previous same duration", "TimeProvider", "Only period-based event counts are comparable");
        Row("source", "", "read_consistency", "", "non-atomic", "queries", "No DB snapshot transaction; data may change during export");
        return [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(sb.ToString())];

        void Row(params string[] values)
        {
            for (var i = 0; i < 7; i++)
            {
                if (i != 0) sb.Append(',');
                var cell = new string((i < values.Length ? values[i] : "")
                    .Where(c => !char.IsControl(c)).Take(450).ToArray());
                var trimmed = cell.TrimStart(' ', '\u00A0');
                if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]))
                    cell = "'" + cell;
                sb.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            sb.Append("\r\n");
        }
    }
}
