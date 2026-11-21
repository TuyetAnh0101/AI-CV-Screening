namespace SmartCv.Core.Entities;

/// <summary>Backend-generated, append-only activity record. Never contains request/response payloads.</summary>
public sealed class AuditEvent
{
    public long AuditEventId { get; set; }
    /// <summary>Workspace của sự kiện; null với sự kiện hệ thống và toàn bộ lịch sử trước A51 (bảng chỉ thêm, không sửa được).</summary>
    public int? WorkspaceId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public int? ActorUserId { get; set; }
    public string? ActorNameSnapshot { get; set; }
    public string? ActorRoleSnapshot { get; set; }
    public string ActionCode { get; set; } = "";
    public string Category { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string? ResourceId { get; set; }
    public string Outcome { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string SafeSummary { get; set; } = "";
    public string? FailureCode { get; set; }
}
