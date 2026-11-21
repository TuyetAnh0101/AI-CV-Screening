using SmartCv.Core.Workspaces;

namespace SmartCv.Core.Entities;

/// <summary>Bảng Jobs - Vị trí tuyển dụng.</summary>
public class Job : IWorkspaceOwned
{
    public int JobId { get; set; }
    /// <summary>Workspace sở hữu. Mặc định là workspace nội bộ cho tới khi A52 đóng dấu tự động.</summary>
    public int WorkspaceId { get; set; } = WorkspaceKinds.InternalWorkspaceId;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal? MinGpa { get; set; }
    public decimal MinYearsExperience { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public int? Headcount { get; set; }
    public DateOnly? ClosingDate { get; set; }

    public ICollection<JobSkill> Skills { get; set; } = new List<JobSkill>();
    public ICollection<Application> Applications { get; set; } = new List<Application>();
}
