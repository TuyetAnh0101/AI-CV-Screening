using SmartCv.Core.Workspaces;

namespace SmartCv.Core.Entities;

/// <summary>Bảng Candidates - Ứng viên.</summary>
public class Candidate : IWorkspaceOwned
{
    public int CandidateId { get; set; }
    /// <summary>Workspace sở hữu. Mặc định chưa gán (UnassignedWorkspaceId = -1); SmartCvDbContext (A52) tự đóng dấu workspace hiện hành khi lưu.</summary>
    public int WorkspaceId { get; set; } = WorkspaceKinds.UnassignedWorkspaceId;
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTime CreatedAt { get; set; }

    // 1 ứng viên có nhiều CV
    public ICollection<Cv> Cvs { get; set; } = new List<Cv>();
}
