extern alias web;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests.RoleNavigation;

public sealed class RoleNavigationExperienceTests
{
    private static ClaimsPrincipal Principal(string role, string? kind = null, string? workspaceRole = null,
        bool verified = true, bool mustChange = false)
    {
        var claims = new List<Claim>
        {
            new("role", role),
            new(AccessRules.SessionClaim, verified ? AccessRules.Verified : AccessRules.Unverified),
            new("smartcv_mcp", mustChange ? "true" : "false")
        };
        if (kind is not null) claims.Add(new(AccessRules.WorkspaceKindClaim, kind));
        if (workspaceRole is not null) claims.Add(new(AccessRules.WorkspaceRoleClaim, workspaceRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AccessRules.AuthenticationType, "name", "role"));
    }

    [Fact]
    public void Persona_requires_verified_workspace_and_never_inferrs_owner_from_hr_role()
    {
        Assert.Equal(WorkspacePersona.SystemAdmin, RoleBasedNavigation.PersonaFor(Principal("Admin")));
        Assert.Equal(WorkspacePersona.InternalHr, RoleBasedNavigation.PersonaFor(Principal("HR", "Internal", "Member")));
        Assert.Equal(WorkspacePersona.PersonalOwner, RoleBasedNavigation.PersonaFor(Principal("HR", "Personal", "Owner")));
        Assert.Equal(WorkspacePersona.None, RoleBasedNavigation.PersonaFor(Principal("HR")));
        Assert.Equal(WorkspacePersona.None, RoleBasedNavigation.PersonaFor(Principal("HR", "Personal", "Member")));
        Assert.Equal(WorkspacePersona.None, RoleBasedNavigation.PersonaFor(Principal("HR", "Internal", "Owner", verified: false)));
        Assert.Equal(WorkspacePersona.None, RoleBasedNavigation.PersonaFor(Principal("HR", "Personal", "Owner", mustChange: true)));
        Assert.Equal(WorkspacePersona.None, RoleBasedNavigation.PersonaFor(Principal("Manager", "Internal", "Owner")));
    }

    [Fact]
    public void Menus_are_role_specific_and_only_include_existing_destinations()
    {
        var admin = RoleBasedNavigation.For(Principal("Admin")).SelectMany(g => g.Items).Select(i => i.Href).ToArray();
        var hr = RoleBasedNavigation.For(Principal("HR", "Internal", "Member")).SelectMany(g => g.Items).Select(i => i.Href).ToArray();
        var owner = RoleBasedNavigation.For(Principal("HR", "Personal", "Owner")).SelectMany(g => g.Items).Select(i => i.Href).ToArray();
        Assert.Contains("admin/dashboard", admin);
        Assert.Contains("admin/ai-usage", admin);
        Assert.Contains("admin/reports", admin);
        Assert.DoesNotContain("account", admin);
        Assert.DoesNotContain("account", hr);
        Assert.DoesNotContain("account", owner);
        Assert.DoesNotContain("upload", admin);
        Assert.DoesNotContain("jobs", admin);
        Assert.Contains("reports/recruitment", hr);
        Assert.Contains("upload", hr);
        Assert.Contains("jobs", hr);
        Assert.Contains("results", owner);
        Assert.DoesNotContain("admin/workspaces", hr);
        Assert.DoesNotContain("users", owner);
        Assert.All(RoleBasedNavigation.For(Principal("HR", "Internal", "Member")), group => Assert.NotEmpty(group.Items));
        Assert.Empty(RoleBasedNavigation.For(Principal("HR")));
        Assert.Empty(RoleBasedNavigation.For(Principal("HR", "Personal", "Member")));
    }

    [Theory]
    [InlineData(null, "Admin", null, null, "/admin/dashboard")]
    [InlineData("/dashboard", "Admin", null, null, "/admin/dashboard")]
    [InlineData("/upload?tab=cv", "Admin", null, null, "/admin/dashboard")]
    [InlineData("/admin/ai-usage", "Admin", null, null, "/admin/ai-usage")]
    [InlineData("/reports", "Admin", null, null, "/reports")]
    [InlineData("/reports/recruitment", "Admin", null, null, "/admin/dashboard")]
    [InlineData("/reports/recruitment", "HR", "Internal", "Member", "/reports/recruitment")]
    [InlineData("/admin/dashboard", "HR", "Personal", "Owner", "/dashboard")]
    [InlineData("/users", "HR", "Internal", "Member", "/dashboard")]
    [InlineData("/results?runId=12", "HR", "Personal", "Owner", "/results?runId=12")]
    [InlineData(null, "HR", "Personal", "Owner", "/dashboard")]
    [InlineData(null, "HR", null, null, "/account")]
    [InlineData("https://untrusted.example", "Admin", null, null, "/admin/dashboard")]
    public void Post_login_destination_blocks_cross_role_navigation(
        string? suggested, string accountRole, string? kind, string? workspaceRole, string expected) =>
        Assert.Equal(expected, RoleBasedNavigation.Destination(suggested, accountRole, kind, workspaceRole));

    [Fact]
    public void Reports_route_is_guarded_by_a_named_policy()
    {
        var page = typeof(RoleReports);
        Assert.Contains(page.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>(), a => a.Template == "/reports");
        Assert.Contains(page.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(), a => a.Policy == AccessRules.ReportsPolicy);
    }
}
