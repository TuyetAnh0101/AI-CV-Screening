namespace SmartCv.Core.Contracts;

/// <summary>A15 run metadata with A16 statistics. Final counters remain null until Completed.</summary>
public class PromptTestRunDto
{
    public int RunId { get; init; }
    public int PromptVersionId { get; init; }
    public string PromptVersionName { get; init; } = string.Empty;
    public int EvaluationSetId { get; init; }
    public string EvaluationSetName { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public decimal Temperature { get; init; }
    public decimal KeywordWeight { get; init; }
    public decimal AiWeight { get; init; }
    public decimal PassThreshold { get; init; }
    public string Status { get; init; } = string.Empty;
    public int TotalItems { get; init; }
    public int ProcessedItems { get; init; }
    public int FailedItems { get; init; }
    public decimal Percent => TotalItems <= 0 ? 100m : Math.Clamp(
        Math.Round(ProcessedItems * 100m / TotalItems, 2, MidpointRounding.AwayFromZero), 0, 100);
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    /// <summary>Completed-run accuracy calculated from this run's evaluations and current HR labels.</summary>
    public decimal? AccuracyPercent { get; init; }
    public string AccuracyStatus => AccuracyPercent is null ? "NotCalculated" : "Stored";
    public int? TotalLabeledCount { get; init; }
    public int? CorrectCount { get; init; }
    public int? MissedCount { get; init; }
    public int? WrongPickCount { get; init; }
    public int? UnscoredCount { get; init; }
    public int? KeywordOnlyCount { get; init; }
}

public sealed class PromptTestProgressDto : PromptTestRunDto
{
    public List<PromptTestEvaluationDto> Items { get; init; } = [];
}

public sealed class PromptTestEvaluationDto
{
    public int EvaluationId { get; init; }
    public int ApplicationId { get; init; }
    public Guid CvId { get; init; }
    public string? CandidateName { get; init; }
    public string FileName { get; init; } = string.Empty;
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    /// <summary>Current HR label; null when it has been removed. The evaluation remains visible.</summary>
    public bool? HrIsPass { get; init; }
    public string? HrNote { get; init; }
    public string Status { get; init; } = string.Empty;
    public string ExtractionStatus { get; init; } = string.Empty;
    public decimal? KeywordScore { get; init; }
    public decimal? AiScore { get; init; }
    public decimal? FinalScore { get; init; }
    public bool? PredictedPass { get; init; }
    public string? ScoreSource { get; init; }
    public string? FitLabel { get; init; }
    public string? Reasoning { get; init; }
    public string? ErrorMessage { get; init; }
    public bool? IsMatch { get; init; }
    public string? MismatchType { get; init; }
}
