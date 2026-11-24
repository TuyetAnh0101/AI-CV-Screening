namespace SmartCv.Core.Contracts;

/// <summary>Provider requests observed by this SCApp deployment only. Not Google project billing.</summary>
public sealed record AdminGeminiTelemetryDto(
    DateTime CapturedAtUtc,
    string Period,
    DateTime? FirstRecordedAtUtc,
    DateTime? LastRecordedAtUtc,
    long Attempts,
    long ScreeningAttempts,
    long PromptTestAttempts,
    long PreviewAttempts,
    long HttpOk,
    long HttpError,
    long NetworkOrTimeout,
    long Cancelled,
    long WithUsageMetadata,
    long WithEstimatedCost,
    long? PromptTokens,
    long? CandidateTokens,
    long? ThoughtsTokens,
    long? TotalTokens,
    decimal? EstimatedCostUsd,
    string ChartBucket,
    int ObservedRpm,
    long? ObservedTpm,
    int ObservedRpd,
    IReadOnlyList<AdminGeminiTelemetryDayDto> Trend,
    IReadOnlyList<AdminGeminiTelemetryModelDto> Models);

public sealed record AdminGeminiTelemetryDayDto(
    DateOnly Date, long Attempts, long HttpOk, long Errors,
    long? PromptTokens, long? CandidateTokens, long? TotalTokens, decimal? CostUsd);

public sealed record AdminGeminiTelemetryModelDto(
    string Model, long Attempts, long HttpOk, long Errors,
    long WithUsageMetadata, long? PromptTokens, long? CandidateTokens,
    long? ThoughtsTokens, long? TotalTokens, decimal? CostUsd);
