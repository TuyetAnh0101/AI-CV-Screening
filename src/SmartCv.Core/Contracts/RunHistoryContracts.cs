namespace SmartCv.Core.Contracts;

public sealed class RunPageDto
{
    public List<RunHistoryItemDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
}

public sealed class RunHistoryItemDto
{
    public int RunId { get; init; }
    public string RunType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int PromptVersionId { get; init; }
    public string PromptVersionName { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public decimal Temperature { get; init; }
    public decimal KeywordWeight { get; init; }
    public decimal AiWeight { get; init; }
    public decimal PassThreshold { get; init; }
    public int? EvaluationSetId { get; init; }
    public string? EvaluationSetName { get; init; }
    public decimal? AccuracyPercent { get; init; }
    public int TotalItems { get; init; }
    public int ProcessedItems { get; init; }
    public int FailedItems { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int? DurationSeconds { get; init; }
    public List<JobCountDto> Jobs { get; init; } = [];
}

public sealed class RunFailuresDto
{
    public int RunId { get; init; }
    public int FailedCount { get; init; }
    public int KeywordOnlyCount { get; init; }
    public List<RunFailureDto> Items { get; init; } = [];
}

public sealed class RunFailureDto
{
    public int EvaluationId { get; init; }
    public int ApplicationId { get; init; }
    public Guid CvId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string? CandidateName { get; init; }
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public string CvExtractionStatus { get; init; } = string.Empty;
    public string? CvExtractionError { get; init; }
    public DateTime CreatedAt { get; init; }
}
