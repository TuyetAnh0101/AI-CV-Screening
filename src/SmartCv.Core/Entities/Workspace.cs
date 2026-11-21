using SmartCv.Core.Workspaces;

namespace SmartCv.Core.Entities;

/// <summary>Không gian làm việc: ranh giới dữ liệu tuyển dụng. WorkspaceId = 1 là workspace nội bộ (Internal).</summary>
public class Workspace
{
    public int WorkspaceId { get; set; }

    /// <summary>Tối đa 100 ký tự.</summary>
    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = WorkspaceKinds.Personal;

    public int? OwnerUserId { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>null = không giới hạn (workspace nội bộ). Quota được áp dụng ở A54.</summary>
    public int? DailyAiEvaluationLimit { get; set; }

    /// <summary>null = không giới hạn.</summary>
    public int? MaxCvCount { get; set; }

    public DateTime CreatedAt { get; set; } // UTC

    public User? Owner { get; set; }

    public ICollection<WorkspaceMember> Members { get; set; } = new List<WorkspaceMember>();
}
