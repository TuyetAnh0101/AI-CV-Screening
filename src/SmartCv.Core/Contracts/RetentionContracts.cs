namespace SmartCv.Core.Contracts;

public sealed class AnonymizeRequest
{
    public string? Actor { get; set; }
}

public sealed class AnonymizeResultDto
{
    public Guid CvId { get; init; }
    public bool AlreadyAnonymized { get; init; }
    public DateTime AnonymizedAt { get; init; }
    public bool FileDeleted { get; init; }
    public bool CandidateDeleted { get; init; }
    public int EvaluationsCleared { get; init; }
}

public sealed class RetentionRunResultDto
{
    public DateTime RanAt { get; init; }
    public bool DryRun { get; init; }
    public int DueCount { get; init; }
    public int AnonymizedCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
    public int RecoveryCount { get; init; }
    public int RecoveredCount { get; init; }
    public List<Guid> RecoveryCvIds { get; init; } = [];
    public List<Guid> CvIds { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}
