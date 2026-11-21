using SmartCv.Core.Workspaces;

namespace SmartCv.Core.Entities;

/// <summary>Bảng Candidates - Ứng viên.</summary>
public class Candidate : IWorkspaceOwned
{
    public int CandidateId { get; set; }
    /// <summary>Workspace sở hữu. Mặc định là workspace nội bộ cho tới khi A52 đóng dấu tự động.</summary>
    public int WorkspaceId { get; set; } = WorkspaceKinds.InternalWorkspaceId;
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTime CreatedAt { get; set; }

    // 1 ứng viên có nhiều CV
    public ICollection<Cv> Cvs { get; set; } = new List<Cv>();
}
