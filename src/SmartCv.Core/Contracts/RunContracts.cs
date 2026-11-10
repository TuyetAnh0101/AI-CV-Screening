namespace SmartCv.Core.Contracts;

public sealed class StartRunRequest
{
    public List<int>? ApplicationIds { get; init; }
}

public sealed record RunProgressDto(
    int RunId, string RunType, string Status, int PromptVersionId, string PromptVersionName,
    string ModelName, decimal Temperature, decimal KeywordWeight, decimal AiWeight,
    decimal PassThreshold, int TotalItems, int ProcessedItems, int FailedItems, decimal Percent,
    DateTime CreatedAt, DateTime? CompletedAt)
{
    public List<RunItemDto> Items { get; init; } = [];
}

public sealed class RunItemDto
{
    public int EvaluationId { get; init; }
    public int ApplicationId { get; init; }
    public Guid CvId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string? CandidateName { get; init; }
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ExtractionStatus { get; init; } = string.Empty;
    public decimal? KeywordScore { get; init; }
    public decimal? AiScore { get; init; }
    public decimal? FinalScore { get; init; }
    public string? ScoreSource { get; init; }
    public string? FitLabel { get; init; }
    public bool? PredictedPass { get; init; }
    public string? ErrorMessage { get; init; }
}
