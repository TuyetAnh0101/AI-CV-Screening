using System.Security.Claims;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Workspaces;

namespace SmartCv.Tests.Auth;

public sealed class RoleBoundaryRulesTests
{
    private static ClaimsPrincipal Principal(int id, string role) => new(new ClaimsIdentity(
        [
            new Claim(SmartCvClaims.UserId, id.ToString()),
            new Claim(SmartCvClaims.Role, role)
        ], "Bearer", SmartCvClaims.Name, SmartCvClaims.Role));

    [Fact]
    public void Admin_is_only_system_admin_never_business_operator()
    {
        var admin = Principal(10, UserRoles.Admin);
        var internalMember = new WorkspaceSessionInfo(1, "Internal", WorkspaceKinds.Internal, WorkspaceRoles.Member);
        Assert.True(RoleBoundaryRules.IsSystemAdmin(admin));
        Assert.False(RoleBoundaryRules.CanOperate(admin, internalMember));
        Assert.False(RoleBoundaryRules.IsPersonalOwner(admin,
            new WorkspaceSessionInfo(2, "Personal", WorkspaceKinds.Personal, WorkspaceRoles.Owner, 10)));
    }

    [Fact]
    public void Internal_hr_with_real_membership_can_recruit()
    {
        var hr = Principal(11, UserRoles.Hr);
        Assert.True(RoleBoundaryRules.CanOperate(hr,
            new WorkspaceSessionInfo(1, "Internal", WorkspaceKinds.Internal, WorkspaceRoles.Member)));
        Assert.True(RoleBoundaryRules.CanOperate(hr,
            new WorkspaceSessionInfo(1, "Internal", WorkspaceKinds.Internal, WorkspaceRoles.Owner)));
        Assert.False(RoleBoundaryRules.IsSystemAdmin(hr));
    }

    [Fact]
    public void Personal_workspace_requires_matching_owner_id_and_owner_membership()
    {
        var owner = Principal(21, UserRoles.Hr);
        var own = new WorkspaceSessionInfo(3, "Mine", WorkspaceKinds.Personal, WorkspaceRoles.Owner, 21);
        Assert.True(RoleBoundaryRules.CanOperate(owner, own));
        Assert.True(RoleBoundaryRules.IsPersonalOwner(owner, own));
        Assert.False(RoleBoundaryRules.CanOperate(owner, own with { OwnerUserId = 999 }));
        Assert.False(RoleBoundaryRules.CanOperate(owner, own with { OwnerUserId = null }));
        Assert.False(RoleBoundaryRules.CanOperate(owner, own with { Role = WorkspaceRoles.Member }));
        Assert.False(RoleBoundaryRules.CanOperate(owner, own with { Kind = "Unknown" }));
        Assert.False(RoleBoundaryRules.CanOperate(owner, null));
    }

    [Fact]
    public void Anonymous_role_or_workspace_claims_do_not_grant_access()
    {
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SmartCvClaims.UserId, "7"), new Claim(SmartCvClaims.Role, UserRoles.Hr)],
            null, SmartCvClaims.Name, SmartCvClaims.Role));
        Assert.False(RoleBoundaryRules.CanOperate(unauthenticated,
            new WorkspaceSessionInfo(1, "Internal", WorkspaceKinds.Internal, WorkspaceRoles.Member)));
        Assert.False(RoleBoundaryRules.CanOperate(Principal(7, "Recruiter"),
            new WorkspaceSessionInfo(1, "Internal", WorkspaceKinds.Internal, WorkspaceRoles.Member)));
    }
}
