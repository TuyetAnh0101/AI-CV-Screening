using System.Security.Claims;
using SmartCv.Core.Auth;
using SmartCv.Core.Workspaces;

namespace SmartCv.Api.Auth;

/// <summary>
/// Role hệ thống quyết định loại công việc. Membership và OwnerUserId lấy từ
/// DbSessionStateStore trong lần xác thực request, không đọc từ client.
/// Không cấp quyền xuyên workspace và không cho Admin làm nghiệp vụ mặc định.
/// </summary>
public static class RoleBoundaryRules
{
    public static bool IsSystemAdmin(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && user.GetRole() == UserRoles.Admin;

    public static bool CanOperate(ClaimsPrincipal? user, WorkspaceSessionInfo? workspace)
    {
        if (user?.Identity?.IsAuthenticated != true || user.GetRole() != UserRoles.Hr ||
            user.GetUserId() is not > 0 || workspace is not { Id: > 0 })
            return false;

        return workspace.Kind switch
        {
            WorkspaceKinds.Internal => workspace.Role is WorkspaceRoles.Member or WorkspaceRoles.Owner,
            WorkspaceKinds.Personal => IsPersonalOwner(user, workspace),
            _ => false
        };
    }

    public static bool IsPersonalOwner(ClaimsPrincipal? user, WorkspaceSessionInfo? workspace) =>
        user?.Identity?.IsAuthenticated == true &&
        user.GetRole() == UserRoles.Hr &&
        user.GetUserId() is > 0 &&
        workspace is
        {
            Id: > 0,
            Kind: WorkspaceKinds.Personal,
            Role: WorkspaceRoles.Owner,
            OwnerUserId: > 0
        } &&
        workspace.OwnerUserId == user.GetUserId();
}
