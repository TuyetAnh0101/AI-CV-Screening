using SmartCv.Core.Workspaces;

namespace SmartCv.Core.Entities;

/// <summary>Quyền thành viên trong workspace (Owner/Member), độc lập với User.Role (Admin/HR).</summary>
public class WorkspaceMember
{
    public int WorkspaceId { get; set; }

    public int UserId { get; set; }

    public string Role { get; set; } = WorkspaceRoles.Member;

    public DateTime JoinedAt { get; set; } // UTC

    public Workspace Workspace { get; set; } = null!;

    public User User { get; set; } = null!;
}
