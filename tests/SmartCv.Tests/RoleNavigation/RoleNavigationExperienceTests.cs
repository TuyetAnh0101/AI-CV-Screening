extern alias web;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
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
    [InlineData("/users", "HR", "Personal", "Owner", "/dashboard")]
    [InlineData("/users?page=2", "Admin", null, null, "/users?page=2")]
    [InlineData("/results?runId=12", "HR", "Personal", "Owner", "/results?runId=12")]
    [InlineData(null, "HR", "Personal", "Owner", "/dashboard")]
    [InlineData(null, "HR", null, null, "/account")]
    [InlineData("https://untrusted.example", "Admin", null, null, "/admin/dashboard")]
    public void Post_login_destination_blocks_cross_role_navigation(
        string? suggested, string accountRole, string? kind, string? workspaceRole, string expected) =>
        Assert.Equal(expected, RoleBasedNavigation.Destination(suggested, accountRole, kind, workspaceRole));

    [Fact]
    public void Account_management_is_in_the_Admin_menu_only()
    {
        var adminGroups = RoleBasedNavigation.For(Principal("Admin"));
        var admin = adminGroups.SelectMany(g => g.Items).ToArray();
        var users = Assert.Single(admin, i => i.Href == "users");
        Assert.Equal("Quản lý tài khoản", users.Text);
        Assert.True(users.AdminOnly);
        Assert.Contains(adminGroups.Single(g => g.Title == "Bảo mật & vận hành").Items, i => i.Href == "users");
        // Same people icon as the existing users/candidates entry (UsersIcon), not a new asset.
        var hrItems = RoleBasedNavigation.For(Principal("HR", "Internal", "Member")).SelectMany(g => g.Items).ToArray();
        Assert.Equal(hrItems.Single(i => i.Href == "candidates").Icon, users.Icon);
        // Other Admin entries are unchanged.
        foreach (var href in new[] { "admin/dashboard", "admin/reports", "permissions", "admin/system-health", "admin/workspaces", "admin/ai-usage", "admin/integrations", "admin/activity-logs" })
            Assert.Contains(href, admin.Select(i => i.Href));

        foreach (var other in new[]
        {
            Principal("HR", "Internal", "Member"), Principal("HR", "Personal", "Owner"), Principal("HR"),
            Principal("Admin", verified: false), Principal("Admin", mustChange: true),
            new ClaimsPrincipal(new ClaimsIdentity())
        })
            Assert.DoesNotContain(RoleBasedNavigation.For(other).SelectMany(g => g.Items), i => i.Href == "users");
    }

    [Fact]
    public async Task Users_page_policy_admits_only_the_verified_Admin()
    {
        var page = typeof(Users);
        Assert.Contains(page.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>(), a => a.Template == "/users");
        var collection = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddLogging();
        web::SmartCv.Web.Services.WebAuthSetup.AddSmartCvWebAuth(collection);   // the Web app's real page policies
        using var services = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(collection);
        var provider = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IAuthorizationPolicyProvider>(services);
        var authorization = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IAuthorizationService>(services);
        var policy = (await AuthorizationPolicy.CombineAsync(provider, page.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>()))!;
        async Task<bool> Can(ClaimsPrincipal user) => (await authorization.AuthorizeAsync(user, null, policy)).Succeeded;
        Assert.True(await Can(Principal("Admin")));
        Assert.False(await Can(Principal("Admin", verified: false)));
        Assert.False(await Can(Principal("Admin", mustChange: true)));
        Assert.False(await Can(Principal("HR", "Internal", "Member")));
        Assert.False(await Can(Principal("HR", "Personal", "Owner")));
        Assert.False(await Can(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public async Task Account_management_API_refuses_HR_and_Personal_Owner_and_serves_the_Admin()
    {
        using var world = await SmartCv.Tests.E2E.B57World.CreateAsync();
        using (var admin = world.AdminClient())
            Assert.Equal(System.Net.HttpStatusCode.OK, (await admin.GetAsync("/api/users?page=1&pageSize=20")).StatusCode);
        foreach (var workspace in new[] { SmartCv.Tests.E2E.B57World.Internal, SmartCv.Tests.E2E.B57World.OwnerAWorkspace })
        {
            using var business = world.Business(workspace);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await business.GetAsync("/api/users?page=1&pageSize=20")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await business.PostAsync("/api/users", System.Net.Http.Json.JsonContent.Create(new { email = "b57-nav@example.invalid", fullName = "Synthetic", role = "HR" }))).StatusCode);
        }
        using var anonymous = world.Anonymous();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public void Reports_route_is_guarded_by_a_named_policy()
    {
        var page = typeof(RoleReports);
        Assert.Contains(page.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>(), a => a.Template == "/reports");
        Assert.Contains(page.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(), a => a.Policy == AccessRules.ReportsPolicy);
    }
}
