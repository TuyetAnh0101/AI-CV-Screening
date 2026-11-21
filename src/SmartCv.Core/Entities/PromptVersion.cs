using SmartCv.Core.Workspaces;

namespace SmartCv.Core.Entities;

/// <summary>Bảng PromptVersions - Phiên bản system prompt. Chỉ một bản được IsActive = true.</summary>
public class PromptVersion : IWorkspaceOwned
{
    public int PromptVersionId { get; set; }
    /// <summary>Workspace sở hữu. Mặc định là workspace nội bộ cho tới khi A52 đóng dấu tự động.</summary>
    public int WorkspaceId { get; set; } = WorkspaceKinds.InternalWorkspaceId;
    public string VersionName { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string? ChangeNote { get; set; }
    public bool IsActive { get; set; } = false;
    public DateTime CreatedAt { get; set; }

    public ICollection<EvaluationRun> EvaluationRuns { get; set; } = new List<EvaluationRun>();
}
