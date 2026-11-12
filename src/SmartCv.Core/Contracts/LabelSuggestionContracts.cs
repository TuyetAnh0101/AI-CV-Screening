namespace SmartCv.Core.Contracts;

/// <summary>A36: a proposed HR label, never persisted by the suggestions endpoint.</summary>
public sealed class LabelSuggestionDto
{
    public int ApplicationId { get; init; }
    public string? CandidateName { get; init; }
    public string FileName { get; init; } = string.Empty;
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string? RejectReason { get; init; }
    public DateTime? StageUpdatedAt { get; init; }
    public bool SuggestedIsPass { get; init; }
    public bool? CurrentLabel { get; init; }
    public string? CurrentNote { get; init; }
    public string? CurrentLabeledBy { get; init; }
    public string Reason { get; init; } = string.Empty;
    /// <summary>New | Same | Conflict, relative to this evaluation set's current label.</summary>
    public string Status { get; init; } = string.Empty;
}

public sealed class LabelSuggestionResponse
{
    public int EvaluationSetId { get; init; }
    public int? JobId { get; init; }
    public int NewCount { get; init; }
    public int SameCount { get; init; }
    public int ConflictCount { get; init; }
    /// <summary>Non-New, non-failed-extraction applications examined but not suggested.</summary>
    public int SkippedCount { get; init; }
    public List<LabelSuggestionDto> Items { get; init; } = [];
}
