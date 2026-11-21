namespace SmartCv.Core.Workspaces;

/// <summary>Quyền thành viên trong workspace. Khác với User.Role (Admin/HR) là quyền hệ thống.</summary>
public static class WorkspaceRoles
{
    public const string Owner = "Owner";
    public const string Member = "Member";

    public static readonly IReadOnlyList<string> All = [Owner, Member];
}
