namespace SmartCv.Core.Workspaces;

/// <summary>Workspace của phiên đăng nhập, đã được xác nhận với database (membership còn hiệu lực, workspace đang Active).</summary>
public sealed record WorkspaceSessionInfo(int Id, string Name, string Kind, string Role);

/// <summary>Chọn workspace khi đăng nhập (MVP: mỗi tài khoản một workspace; nếu có nhiều thì ưu tiên Internal).</summary>
public static class WorkspaceSelectionRules
{
    public static WorkspaceSessionInfo? Choose(IEnumerable<WorkspaceSessionInfo> activeMemberships) => activeMemberships
        .OrderBy(w => w.Kind == WorkspaceKinds.Internal ? 0 : 1).ThenBy(w => w.Id).FirstOrDefault();
}
