namespace SmartCv.Core.Contracts;

public sealed class StartRunRequest
{
    public List<int>? ApplicationIds { get; init; }
}

public sealed record RunProgressDto(
    int RunId, string RunType, string Status, int PromptVersionId, string PromptVersionName,
    string ModelName, decimal Temperature, decimal KeywordWeight, decimal AiWeight,
    decimal PassThreshold, int TotalItems, int ProcessedItems, int FailedItems, decimal Percent,
    DateTime CreatedAt, DateTime? CompletedAt);
