using System.Globalization;
using System.Text;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Reports;

/// <summary>Management metadata only; no candidate, CV, prompts, emails or row-level scores.</summary>
public static class AdminOperationsReportCsvWriter
{
    public const string ContentType = "text/csv; charset=utf-8";
    public static string FileName(DateTime utc) =>
        $"scapp-admin-operations-{utc.AddHours(7):yyyyMMdd-HHmmss}.csv";

    public static byte[] Write(AdminReportSummaryDto report, AdminReportTrendDto trend,
        IReadOnlyList<AdminReportExportEventDto> exports)
    {
        var sb = new StringBuilder();
        Row(sb, "section", "local_date", "metric", "dimension", "value",
            "actor_user_id", "workspace_id", "format", "row_count", "source",
            "occurred_at_utc", "actor_role", "anonymous");
        void Metric(string section, string metric, long value, string source, string dimension = "") =>
            Row(sb, section, "", metric, dimension, value.ToString(CultureInfo.InvariantCulture),
                "", "", "", "", source);
        void Status(string section, string metric, string value, string source) =>
            Row(sb, section, "", metric, "", value, "", "", "", "", source);

        Status("meta", "period", $"{report.Period.From:yyyy-MM-dd}/{report.Period.To:yyyy-MM-dd}",
            "Asia/Ho_Chi_Minh, inclusive local dates");
        Status("meta", "captured_at_utc", report.Period.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture), "TimeProvider");
        Metric("accounts", "total_now", report.Accounts.TotalNow, "Users");
        Metric("accounts", "enabled_now", report.Accounts.EnabledNow, "Users.IsActive");
        Metric("accounts", "locked_now", report.Accounts.LockedNow, "Users.IsActive=false");
        Metric("accounts", "created_in_period", report.Accounts.CreatedInPeriod, "Users.CreatedAt");
        foreach (var item in report.Accounts.BySignupSource)
            Metric("accounts", "signup_source", item.Count, "Users.SignupSource", item.Code);
        Metric("workspaces", "total_now", report.Workspaces.TotalNow, "Workspaces");
        Metric("workspaces", "active_now", report.Workspaces.ActiveNow, "Workspaces.IsActive");
        Metric("workspaces", "inactive_now", report.Workspaces.InactiveNow, "Workspaces.IsActive=false");
        Metric("workspaces", "personal_with_ai_limit_now", report.Workspaces.PersonalWithDailyAiLimitNow,
            "Workspaces.DailyAiEvaluationLimit");
        Metric("workspaces", "personal_with_cv_limit_now", report.Workspaces.PersonalWithCvLimitNow,
            "Workspaces.MaxCvCount");
        foreach (var item in report.Workspaces.ByKind)
            Metric("workspaces", "kind", item.Count, "Workspaces.Kind", item.Code);
        Metric("ai", "net_reserved_units", report.Ai.NetReservedUnits, "AiUsageEntries.Units-UnitsReleased");
        Status("ai", "quota_exceeded_events", "not_available", report.Ai.QuotaExceededSourceStatus);
        Metric("ai", "failed_runs", report.Ai.FailedRuns, "EvaluationRuns.Status");
        Metric("ai", "failed_evaluations", report.Ai.FailedEvaluations, "Evaluations.Status");
        Status("ai", "gemini_telemetry_status", report.Ai.ProviderSourceStatus, "GeminiInvocationLogs");
        if (report.Ai.GeminiAttempts is { } attempts)
            Metric("ai", "gemini_attempts", attempts, "GeminiInvocationLogs");
        if (report.Ai.GeminiErrorsOrTimeouts is { } failures)
            Metric("ai", "gemini_errors_or_timeouts", failures, "GeminiInvocationLogs");
        Metric("security", "failed_password_logins", report.Security.FailedPasswordLogins, "AuditEvents.auth.login");
        Metric("security", "rejected_external_callbacks", report.Security.RejectedExternalCallbacks, "AuditEvents.auth.external_rejected");
        Metric("security", "denied_audit_events", report.Security.DeniedAuditEvents, "AuditEvents.Outcome");
        Metric("security", "access_denied_audit_events", report.Security.AccessDeniedAuditEvents, "AuditEvents.FailureCode");
        Metric("security", "admin_audit_events", report.Security.AdministrativeAuditEvents, "AuditEvents.ActorRoleSnapshot");
        Metric("exports", "ranking_files", report.Exports.RankingFiles, "ExportLogs");
        Metric("exports", "ranking_rows", report.Exports.RankingRows, "ExportLogs.RowCount");
        Metric("exports", "hr_aggregate_files", report.Exports.HrAggregateFiles, "AuditEvents");
        Status("backup", "last_completed_at", "not_available", report.Backup.Source);

        foreach (var day in trend.Points)
        {
            var date = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            void Point(string metric, long value, string source) =>
                Row(sb, "daily", date, metric, "", value.ToString(CultureInfo.InvariantCulture),
                    "", "", "", "", source);
            Point("screening_reserved", day.ScreeningUnits, "AiUsageEntries");
            Point("prompt_test_reserved", day.PromptTestUnits, "AiUsageEntries");
            Point("preview_reserved", day.PreviewUnits, "AiUsageEntries");
            Point("failed_runs", day.FailedRuns, "EvaluationRuns");
            Point("denied_events", day.DeniedAuditEvents, "AuditEvents");
            Row(sb, "daily", date, "quota_exceeded_events", "", "not_available", "", "", "", "",
                "No complete persisted quota-rejection counter");
        }
        foreach (var item in exports)
            Row(sb, "export_event", "", "export", item.Category, "",
                item.ActorUserId?.ToString(CultureInfo.InvariantCulture) ?? "",
                item.WorkspaceId?.ToString(CultureInfo.InvariantCulture) ?? "",
                item.Format, item.RowCount?.ToString(CultureInfo.InvariantCulture) ?? "",
                $"AuditEvents#{item.AuditEventId};{item.RowCountSource}",
                item.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                item.ActorRole ?? "", item.Anonymous?.ToString() ?? "");

        // UTF-8 BOM is explicitly included to make Excel display Vietnamese correctly.
        return [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static void Row(StringBuilder sb, params string[] cells)
    {
        for (var i = 0; i < 13; i++)
        {
            if (i != 0) sb.Append(',');
            // Neutralize spreadsheet formula starts; forbid CR/LF/control injection.
            var text = new string((i < cells.Length ? cells[i] : "").Where(c => !char.IsControl(c)).Take(400).ToArray());
            var formulaCandidate = text.TrimStart(' ', '\u00a0');
            if (formulaCandidate.Length > 0 && "=+-@".Contains(formulaCandidate[0]))
                text = "'" + text;
            sb.Append('"').Append(text.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        sb.Append("\r\n");
    }
}
