extern alias web;

using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using web::SmartCv.Web.Components.Pages;
using JobsPage = web::SmartCv.Web.Components.Pages.Jobs;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
using PrivacyPage = web::SmartCv.Web.Components.Pages.Privacy;

namespace SmartCv.Tests;

/// <summary>
/// B28: bảng quyền giao diện. (1) AccessRules suy ra đúng trạng thái và fail-closed; (2) MỌI trang có @page được
/// AuthorizeRouteView chặn / mở đúng như A29 — duyệt bằng phản chiếu nên trang mới thêm vào cũng bị kiểm tra;
/// (3) menu theo vai trò không có nhóm rỗng, không có mục chỉ Admin cho HR.
/// </summary>
public class B28AccessRulesTests
{
    public static readonly TheoryData<string> States = new() { "anonymous", "unverified", "mcp", "norole", "HR", "Admin" };

    private static ClaimsPrincipal State(string state) => state switch
    {
        "anonymous" => AccessRules.Nobody,
        "unverified" => B28Auth.Principal("Admin", verified: false),
        "mcp" => B28Auth.Principal("HR", mustChange: true),
        "norole" => B28Auth.Principal("Manager"),
        _ => B28Auth.Principal(state)
    };

    [Theory]
    [InlineData("anonymous", AccessLevel.Anonymous)]
    [InlineData("unverified", AccessLevel.Unverified)]
    [InlineData("mcp", AccessLevel.PasswordChangeRequired)]
    [InlineData("norole", AccessLevel.NoValidRole)]
    [InlineData("HR", AccessLevel.Hr)]
    [InlineData("Admin", AccessLevel.Admin)]
    public void Level_distinguishes_every_session_state(string state, AccessLevel expected) =>
        Assert.Equal(expected, AccessRules.Level(State(state)));

    [Fact]
    public void Unverified_session_never_carries_a_role_even_when_the_stored_user_was_admin()
    {
        var user = State("unverified");
        Assert.True(user.Identity!.IsAuthenticated);
        Assert.False(user.IsInRole("Admin"));
        Assert.False(AccessRules.CanUseApp(user));
        Assert.All(Enum.GetValues<AppPermission>(), p => Assert.False(AccessRules.Can(user, p)));
    }

    [Fact]
    public void Client_declared_identities_are_not_trusted()
    {
        // Không do ApiAuthenticationStateProvider dựng (loại xác thực khác, thiếu cờ phiên đã xác minh) → không có quyền.
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "Admin"), new Claim("name", "x")], "Cookies", "name", "role"));
        var noSession = new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "Admin")], AccessRules.AuthenticationType, "name", "role"));
        var both = new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "Admin"), new Claim("role", "HR"), new Claim(AccessRules.SessionClaim, AccessRules.Verified)], AccessRules.AuthenticationType, "name", "role"));
        Assert.Equal(AccessLevel.Anonymous, AccessRules.Level(forged));
        Assert.Equal(AccessLevel.Unverified, AccessRules.Level(noSession));
        Assert.Equal(AccessLevel.NoValidRole, AccessRules.Level(both));
        Assert.Equal(AccessLevel.Anonymous, AccessRules.Level(null));
    }

    [Theory]
    [MemberData(nameof(States))]
    public void Business_and_admin_actions_follow_the_approved_role_matrix(string state)
    {
        var user = State(state);
        foreach (var permission in Enum.GetValues<AppPermission>())
        {
            var adminOnly = permission is AppPermission.ManageUsers or AppPermission.ViewAudit;
            Assert.Equal(adminOnly && state == "Admin", AccessRules.Can(user, permission)); // no HR workspace in the legacy fixture
        }
    }

    [Fact]
    public void Pending_or_missing_state_is_fail_closed()
    {
        var pending = new TaskCompletionSource<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>().Task;
        Assert.Same(AccessRules.Nobody, AccessRules.Current(null));
        Assert.Same(AccessRules.Nobody, AccessRules.Current(pending));
        Assert.False(AccessRules.Can(pending, AppPermission.ManageJobs));
        Assert.False(AccessRules.Can(Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(State("Admin"))), AppPermission.ManageJobs));
    }

    // ---------- Trang: AuthorizeRouteView ----------

    // B51/B52: Các trang được phép truy cập công khai.
    private static readonly Type[] PublicPages =
    [
        typeof(Login),
        typeof(NotFound),
        typeof(Error),
        typeof(Landing),
        typeof(Terms),
        typeof(PrivacyPage)
    ];

    // Các trang chỉ dành cho Admin.
    private static readonly Type[] AdminPages =
    [
        typeof(AdminOperationsDashboard),
        typeof(AdminSystemHealth),
        typeof(AdminWorkspaces),
        typeof(AdminAiUsage),
        typeof(AdminIntegrations),
        typeof(Users),
        typeof(ActivityLogs),
        typeof(web::SmartCv.Web.Components.Pages.AdminDashboard)
    ];
    // private static readonly Type[] AdminPages = [typeof(Users), typeof(ActivityLogs), typeof(web::SmartCv.Web.Components.Pages.AdminDashboard)];

    private static IEnumerable<Type> RoutablePages() => typeof(Login).Assembly.GetTypes()
        .Where(t => typeof(IComponent).IsAssignableFrom(t) && t.GetCustomAttributes<RouteAttribute>().Any());

    /// <summary>Giống AuthorizeRouteView: [AllowAnonymous] → mở; còn lại gộp mọi [Authorize] rồi đánh giá chính sách của Web.</summary>
    private static async Task<bool> CanOpen(Type page, ClaimsPrincipal user)
    {
        var attributes = page.GetCustomAttributes(inherit: true);
        if (attributes.OfType<IAllowAnonymous>().Any()) return true;
        await using var sp = new ServiceCollection().AddLogging().AddSmartCvWebAuth().BuildServiceProvider();
        var data = attributes.OfType<IAuthorizeData>().ToArray();
        Assert.NotEmpty(data);   // fail-closed: không trang nào "quên" phân quyền
        var policy = await AuthorizationPolicy.CombineAsync(sp.GetRequiredService<IAuthorizationPolicyProvider>(), data);
        return (await sp.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, resource: null, policy!)).Succeeded;
    }

    [Fact]
    public void Every_routable_page_is_protected_unless_explicitly_public()
    {
        var pages = RoutablePages().ToList();
        Assert.True(pages.Count >= 15, "Không tìm thấy trang Blazor nào?");
        var anonymous = pages.Where(p => p.GetCustomAttributes(true).OfType<IAllowAnonymous>().Any()).ToHashSet();
        Assert.Equal(PublicPages.ToHashSet(), anonymous);
        Assert.All(pages.Except(anonymous), p => Assert.NotEmpty(p.GetCustomAttributes(true).OfType<IAuthorizeData>()));
        Assert.Contains(typeof(ForbiddenPage), pages);
    }

    [Theory]
    [MemberData(nameof(States))]
    public async Task Route_matrix_matches_A29(string state)
    {
        var user = State(state);
        var business = state is "HR" or "Admin";
        foreach (var page in RoutablePages())
        {
            var expected = PublicPages.Contains(page)
                || (page == typeof(web::SmartCv.Web.Components.Account.ChangePassword) ? state is "HR" or "Admin" or "mcp"   // B29: SignedIn
                : AdminPages.Contains(page) ? state == "Admin"
                : page == typeof(RoleReports) ? state == "Admin"
                : page.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy == AccessRules.BusinessPolicy) ? false
                : business);
            Assert.True(expected == await CanOpen(page, user), $"{page.Name} / {state}: mong đợi {(expected ? "MỞ" : "CHẶN")}");
        }
    }

    [Fact]
    public void Job_editor_and_list_use_the_protected_business_policy()
    {
        var routes = typeof(JobEdit).GetCustomAttributes<RouteAttribute>().Select(r => r.Template).ToList();
        Assert.Equal(["/jobs/new", "/jobs/{Id:int}"], routes);
        Assert.NotEmpty(typeof(JobEdit).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Contains(typeof(JobEdit).GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == AccessRules.BusinessPolicy);
        Assert.Contains(typeof(JobsPage).GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == AccessRules.BusinessPolicy);
    }

    [Fact]
    public async Task Http_step_passes_through_because_the_token_lives_in_the_circuit()
    {
        var called = false;
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        await new PassThroughAuthorizationResultHandler().HandleAsync(_ => { called = true; return Task.CompletedTask; }, context,
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(), Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult.Challenge());
        Assert.True(called);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public void Wiring_uses_AuthorizeRouteView_and_the_pass_through_middleware_before_antiforgery()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SmartCv.Web"));
        var routes = File.ReadAllText(Path.Combine(root, "Components", "Routes.razor"));
        Assert.Contains("<AuthorizeRouteView", routes);
        Assert.DoesNotContain("<RouteView ", routes);
        Assert.Contains("<NotAuthorizedContent User=\"context.User\" />", routes);
        var program = File.ReadAllText(Path.Combine(root, "Program.cs"));
        Assert.True(program.IndexOf("app.UseSmartCvWebAuth();", StringComparison.Ordinal) is > 0 and var i
                    && i < program.IndexOf("app.UseAntiforgery();", StringComparison.Ordinal));
        Assert.Contains("@attribute [Authorize]", File.ReadAllText(Path.Combine(root, "Components", "Pages", "_Imports.razor")));
    }

    // ---------- Menu ----------

    private static ClaimsPrincipal ValidHr()
    {
        var baseUser = B28Auth.Principal("HR");
        var claims = baseUser.Claims.Concat(new[]
        {
            new Claim(AccessRules.WorkspaceKindClaim, "Internal"),
            new Claim(AccessRules.WorkspaceRoleClaim, "Member")
        });
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AccessRules.AuthenticationType, "name", "role"));
    }

    [Theory]
    [InlineData("HR")]
    [InlineData("Admin")]
    public async Task Menu_shows_only_reachable_pages_and_never_an_empty_group(string role)
    {
        var user = role == "HR" ? ValidHr() : B28Auth.Principal("Admin");
        var groups = AppNavigation.For(user);
        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.NotEmpty(g.Items));
        if (role == "HR") Assert.All(groups.SelectMany(g => g.Items), i => Assert.False(i.AdminOnly));
        var pages = RoutablePages().SelectMany(p => p.GetCustomAttributes<RouteAttribute>()
            .Select(r => (Page: p, Path: r.Template.Trim('/')))).ToList();
        foreach (var item in groups.SelectMany(g => g.Items))
        {
            var target = pages.FirstOrDefault(p => p.Path.Equals(item.Href, StringComparison.OrdinalIgnoreCase));
            Assert.True(target.Page is not null, $"Menu {item.Text} points to missing /{item.Href}");
            Assert.True(await CanOpen(target.Page, user), $"{role} sees {item.Text} but route denies it");
        }
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("unverified")]
    [InlineData("mcp")]
    [InlineData("norole")]
    public void Menu_is_empty_for_anyone_who_cannot_use_the_app(string state) => Assert.Empty(AppNavigation.For(State(state)));

    [Fact]
    public void Menu_groups_all_existing_business_destinations_under_recruitment_and_AI()
    {
        var hrefs = AppNavigation.For(ValidHr()).SelectMany(g => g.Items).Select(i => i.Href).ToList();
        Assert.Contains("dashboard", hrefs);
        Assert.Contains("reports", hrefs);
        Assert.Contains("jobs", hrefs);
        Assert.Contains("results", hrefs);
        Assert.Contains("account", hrefs);
        Assert.DoesNotContain("admin/dashboard", hrefs);
    }

    [Fact]
    public void Admin_has_a_distinct_home_and_shares_the_same_business_routes_with_HR()
    {
        var admin = AppNavigation.For(B28Auth.Principal("Admin"));
        var hr = AppNavigation.For(ValidHr());
        Assert.Equal("admin/dashboard", admin[0].Items[0].Href);
        Assert.Equal("Dashboard quản trị", admin[0].Items[0].Text);
        Assert.Equal("Dashboard tuyển dụng", hr[0].Items[0].Text);
        Assert.DoesNotContain(admin.SelectMany(g => g.Items), i => i.Href is "jobs" or "upload" or "results");
        Assert.DoesNotContain(hr.SelectMany(g => g.Items), i => i.Href.StartsWith("admin/", StringComparison.Ordinal));
        Assert.Equal("/admin/dashboard", AppNavigation.HomeFor(B28Auth.Principal("Admin")));
        Assert.Equal("/dashboard", AppNavigation.HomeFor(B28Auth.Principal("HR")));
    }

    [Theory]
    [InlineData("jobs/new", "jobs")]
    [InlineData("jobs/12?x=1", "jobs")]
    [InlineData("jobs", null)]
    [InlineData("forbidden", null)]
    [InlineData("unknown/thing", null)]
    [InlineData("", null)]
    public void Forbidden_page_back_link_is_an_in_app_parent(string path, string? expected) =>
        Assert.Equal(expected, AppNavigation.ParentFor(path, ValidHr())?.Href);
}
