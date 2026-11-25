namespace SmartCv.Core.Contracts;

/// <summary>All dates are inclusive Vietnam-local calendar dates; all event timestamps remain UTC.</summary>
public sealed record AdminReportPeriodDto(DateOnly From, DateOnly To, string TimeZone, DateTime CapturedAtUtc);
public sealed record AdminReportDimensionDto(string Code, long Count);
public sealed record AdminReportAccountStatsDto(
    long TotalNow, long EnabledNow, long LockedNow, long CreatedInPeriod,
    IReadOnlyList<AdminReportDimensionDto> BySignupSource);
public sealed record AdminReportWorkspaceStatsDto(
    long TotalNow, long ActiveNow, long InactiveNow, long PersonalNow, long InternalNow,
    long PersonalWithDailyAiLimitNow, long PersonalWithCvLimitNow,
    IReadOnlyList<AdminReportDimensionDto> ByKind);

/// <summary>Ledger units are net reservations (Units-UnitsReleased), never tokens or Google quota.</summary>
public sealed record AdminReportAiStatsDto(
    long NetReservedUnits, long ScreeningUnits, long PromptTestUnits, long PreviewUnits,
    long? QuotaExceededEvents, string QuotaExceededSourceStatus, long FailedRuns, long FailedEvaluations,
    bool ProviderTelemetryAvailable, long? GeminiAttempts, long? GeminiHttpOk,
    long? GeminiErrorsOrTimeouts, long? GeminiCancelled, string ProviderSourceStatus);

/// <summary>Counts are persisted AuditEvents; denied events do not equal all HTTP 403 requests.</summary>
public sealed record AdminReportSecurityStatsDto(
    long FailedPasswordLogins, long RejectedExternalCallbacks, long DeniedAuditEvents,
    long AccessDeniedAuditEvents, long AdministrativeAuditEvents);

public sealed record AdminReportExportStatsDto(long RankingFiles, long HrAggregateFiles,
    long RankingRows, string RowCountCoverage);
public sealed record AdminReportBackupStatsDto(string Status, DateTime? LastCompletedAtUtc, string Source);
public sealed record AdminReportSourceNoteDto(string Metric, string Source, string Basis);
public sealed record AdminReportSummaryDto(AdminReportPeriodDto Period, AdminReportAccountStatsDto Accounts,
    AdminReportWorkspaceStatsDto Workspaces, AdminReportAiStatsDto Ai,
    AdminReportSecurityStatsDto Security, AdminReportExportStatsDto Exports,
    AdminReportBackupStatsDto Backup)
{
    /// <summary>Stable provenance hints for the future UI; never imply a live monitoring SLA.</summary>
    public IReadOnlyList<AdminReportSourceNoteDto> Sources { get; init; } =
    [
        new("accounts", "Users", "Current IsActive plus CreatedAt within the requested window"),
        new("signup", "Users.SignupSource", "Original registration source, not linked login providers"),
        new("workspaces", "Workspaces", "Current IsActive/Kind and configured limits"),
        new("ai_ledger", "AiUsageEntries", "Net reserved units by CreatedAtUtc, not provider tokens"),
        new("ai_provider", "GeminiInvocationLogs", "Attempt records including retries; nullable if unavailable"),
        new("quota_rejections", "not_instrumented", "No reliable persistent rejection counter"),
        new("failed_runs", "EvaluationRuns/Evaluations", "Current statuses for runs created in the period"),
        new("security", "AuditEvents", "Persisted security events only, not total HTTP request counts"),
        new("ranking_exports", "ExportLogs", "RowCount available for ranking files"),
        new("hr_exports", "AuditEvents", "Aggregate report export count; row counts not structured"),
        new("backup", "not_available", "Pending future A62 evidence")
    ];
}

public sealed record AdminReportDayDto(DateOnly Date, long ScreeningUnits, long PromptTestUnits,
    long PreviewUnits, long FailedRuns, long DeniedAuditEvents, long? QuotaExceededEvents)
{
    public long NetReservedUnits => ScreeningUnits + PromptTestUnits + PreviewUnits;
}
public sealed record AdminReportTrendDto(AdminReportPeriodDto Period, IReadOnlyList<AdminReportDayDto> Points,
    string LedgerSource, string FailedRunSource, string DenialSource);

/// <summary>Only operational metadata; never exposes CV, candidate, prompt, email or search strings.</summary>
public sealed record AdminReportExportEventDto(long AuditEventId, DateTime OccurredAtUtc,
    int? ActorUserId, string? ActorRole, int? WorkspaceId,
    string Category, string Format, int? RowCount, bool? Anonymous, string RowCountSource);
public sealed record AdminReportExportPageDto(AdminReportPeriodDto Period, int Page,
    int PageSize, long Total, IReadOnlyList<AdminReportExportEventDto> Items);
public sealed record AdminReportExportPreviewDto(AdminReportPeriodDto Period,
    long ExportEventCount, int MaxExportEvents, bool CanDownload,
    string? BlockCode, string Format);

/// <summary>Current readiness and flags only. No historical uptime, no inferred worker liveness.</summary>
public sealed record AdminReportWorkerFlagDto(string Name, bool Enabled, string Meaning);
public sealed record AdminReportHealthDto(DateTime CheckedAtUtc, string ReadinessStatus,
    IReadOnlyList<SmartCv.Core.Ops.HealthCheckResult> Checks,
    IReadOnlyList<AdminReportWorkerFlagDto> WorkerConfiguration,
    string ApiUptimeStatus, string EmailCheckScope, string BackupStatus);
