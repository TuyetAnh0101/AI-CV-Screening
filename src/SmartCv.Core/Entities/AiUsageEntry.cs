using SmartCv.Core.Workspaces;

namespace SmartCv.Core.Entities;

/// <summary>
/// Giữ trước năng lực AI lúc nhận yêu cầu. UnitsReleased chỉ tăng khi chứng minh được AI chưa được gọi.
/// Một entry cho mỗi run; một entry cho mỗi preview. Đây là ledger vận hành, không phải AuditEvent.
/// </summary>
public sealed class AiUsageEntry : IWorkspaceOwned
{
    public long AiUsageEntryId { get; set; }
    public int WorkspaceId { get; set; } = WorkspaceKinds.UnassignedWorkspaceId;
    public string Source { get; set; } = string.Empty; // Screening | PromptTest | Preview
    public int? RunId { get; set; } // null khi Preview
    public int Units { get; set; }
    public int UnitsReleased { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
