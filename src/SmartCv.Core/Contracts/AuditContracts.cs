namespace SmartCv.Core.Contracts;

public sealed record AuditEventDto(long AuditEventId, DateTime OccurredAtUtc, int? ActorUserId,
    string? ActorNameSnapshot, string? ActorRoleSnapshot, string ActionCode, string Category,
    string ResourceType, string? ResourceId, string Outcome, string RequestId, string SafeSummary, string? FailureCode)
{
    public int? WorkspaceId { get; init; }
    public string? WorkspaceName { get; init; }
}

public sealed class AuditPageDto
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int LoginCount { get; init; }
    public int AccountActionCount { get; init; }
    public int FailureCount { get; init; }
    public List<AuditEventDto> Items { get; init; } = [];
}
