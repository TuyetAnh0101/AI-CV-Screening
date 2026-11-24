namespace SmartCv.Core.Contracts;

// API contracts contain only operational statistics. No CV body, candidate identity,
// secret, API key, token, email or OAuth credential is returned by these endpoints.
public sealed record AdminSystemOverviewDto(
    DateTime CapturedAtUtc,
    long TotalAccounts, long ActiveAccounts,
    long ActiveWorkspaces, long PersonalWorkspaces,
    long TotalJobs, long ActiveJobs,
    long StoredCvs, long TotalApplications,
    long TotalRuns, long PendingRuns, long FailedRuns24h,
    long AiReservedUnitsToday, long RejectedAuditEvents24h);

public sealed record AdminAiUsagePointDto(
    DateOnly Date,
    long ScreeningUnits, long PromptTestUnits, long PreviewUnits)
{
    public long Total => ScreeningUnits + PromptTestUnits + PreviewUnits;
}

public sealed record AdminAiUsageDto(
    DateTime CapturedAtUtc,
    int Days,
    IReadOnlyList<AdminAiUsagePointDto> Points,
    long TotalReservedUnits,
    bool HasTokenTelemetry = false,
    long? InputTokens = null,
    long? OutputTokens = null,
    decimal? EstimatedCostUsd = null);

public sealed record AdminWorkspaceRowDto(
    int WorkspaceId,
    string Name,
    string Kind,
    bool IsActive,
    int? DailyAiEvaluationLimit,
    int? MaxCvCount,
    long UsedAiUnitsToday,
    long StoredCvs,
    long TotalRuns);

public sealed record AdminWorkspacePageDto(
    int Page, int PageSize, long Total,
    IReadOnlyList<AdminWorkspaceRowDto> Items);

/// <summary>
/// New limits are required integers, not null. Zero means forbid NEW usage, not erase old data.
/// Expected limits are nullable to support historical Personal workspaces with null columns.
/// Reason is an approved operational code; no arbitrary text or candidate details.
/// </summary>
public sealed record AdminQuotaChangeRequest(
    [property: System.Text.Json.Serialization.JsonRequired] int? ExpectedDailyAiEvaluationLimit,
    [property: System.Text.Json.Serialization.JsonRequired] int? ExpectedMaxCvCount,
    [property: System.Text.Json.Serialization.JsonRequired] int DailyAiEvaluationLimit,
    [property: System.Text.Json.Serialization.JsonRequired] int MaxCvCount,
    [property: System.Text.Json.Serialization.JsonRequired] string Reason);

public sealed record AdminIntegrationSnapshotDto(
    DateTime CapturedAtUtc,
    string ScoringModel,
    bool GeminiKeyConfigured,
    bool GeminiFakeMode,
    int GeminiTimeoutSeconds,
    int GeminiMinDelayMs,
    bool GoogleLoginEnabled,
    bool GoogleClientConfigured,
    string? GoogleClientIdHint,
    string GoogleCallbackPath,
    string VerificationStatus);

public sealed record AdminQuotaChangeResultDto(
    string Status, int WorkspaceId,
    int? DailyAiEvaluationLimit = null, int? MaxCvCount = null);


/// <summary>Actual persisted SCApp data. Seven days is a rolling UTC window, not provider quota.
/// AiScoredEvaluations7Days includes any historical simulated evaluations: it is NOT a Gemini API call counter.
/// Google counts come from existing append-only AuditEvents.
/// </summary>
public sealed record AdminProviderInsightsDto(
    DateTime CapturedAtUtc,
    long GoogleLinkedAccounts,
    long GoogleSuccessfulCallbacks7Days,
    long GoogleNewAccounts7Days,
    long GoogleRejectedCallbacks7Days,
    DateTime? LastGoogleSuccessfulCallbackUtc,
    long AiScoredEvaluations7Days,
    long AiReservedUnits7Days);

/// <summary>Result of an explicit Google models.get probe. Never contains secrets or response body.</summary>
public sealed record AdminGeminiCheckDto(
    DateTime CheckedAtUtc,
    string Model,
    string State,
    int? ProviderHttpStatus,
    bool SupportsGenerateContent,
    string Detail);

/// <summary>
/// Historical SCApp outcomes. These are persisted evaluation records, not confirmed
/// Google API requests, not provider quota and not usageMetadata token counts.
/// Completed runs can still contain Failed evaluations.
/// </summary>
public sealed record AdminAiHistoryDto(
    DateTime CapturedAtUtc,
    string Period,
    string ChartBucket,
    DateOnly? FromDate,
    DateOnly? ToDate,
    long TotalRuns,
    long CompletedRuns,
    long FailedRuns,
    long ActiveRuns,
    long TotalEvaluations,
    long CombinedEvaluations,
    long KeywordOnlyEvaluations,
    long FailedEvaluations,
    long OtherEvaluations,
    long EvaluationsWithAiScore,
    long ScreeningEvaluations,
    long PromptTestEvaluations,
    DateTime? FirstRunAtUtc,
    DateTime? LastRunAtUtc,
    IReadOnlyList<AdminAiHistoryModelDto> Models,
    IReadOnlyList<AdminAiHistoryPointDto> Trend);

public sealed record AdminAiHistoryModelDto(
    string Model,
    long Runs,
    long CompletedRuns,
    long FailedRuns,
    long Evaluations,
    long Combined,
    long KeywordOnly,
    long Failed,
    long Other,
    long WithAiScore);

public sealed record AdminAiHistoryPointDto(
    DateOnly Bucket, long Combined, long KeywordOnly, long Failed, long Other)
{
    public long Total => Combined + KeywordOnly + Failed + Other;
}
