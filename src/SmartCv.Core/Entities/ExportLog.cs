using SmartCv.Core.Workspaces;

namespace SmartCv.Core.Entities;

/// <summary>Append-only export metadata, linked to B43's canonical actor/time event. No search text or CV data.</summary>
public sealed class ExportLog : IWorkspaceOwned
{
    public long ExportId { get; set; }
    /// <summary>Workspace sở hữu. Mặc định chưa gán (UnassignedWorkspaceId = -1); SmartCvDbContext (A52) tự đóng dấu workspace hiện hành khi lưu.</summary>
    public int WorkspaceId { get; set; } = WorkspaceKinds.UnassignedWorkspaceId;
    public long AuditEventId { get; set; }
    public AuditEvent AuditEvent { get; set; } = null!;
    public int RunId { get; set; }
    public int JobId { get; set; }
    public string Format { get; set; } = "xlsx";
    public string Stage { get; set; } = "";
    public string Fit { get; set; } = "";
    public bool SearchApplied { get; set; }
    public bool NeedsReview { get; set; }
    public bool ManualFlags { get; set; }
    public int RowCount { get; set; }
    public bool Anonymized { get; set; }
}
