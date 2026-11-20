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
    public void Every_admin_only_action_is_admin_only(string state)
    {
        var user = State(state);
        Assert.All(Enum.GetValues<AppPermission>(), p => Assert.Equal(state == "Admin", AccessRules.Can(user, p)));
    }

    [Fact]
    public void Pending_or_missing_state_is_fail_closed()
    {
        var pending = new TaskCompletionSource<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>().Task;
        Assert.Same(AccessRules.Nobody, AccessRules.Current(null));
        Assert.Same(AccessRules.Nobody, AccessRules.Current(pending));
        Assert.False(AccessRules.Can(pending, AppPermission.ManageJobs));
        Assert.True(AccessRules.Can(Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(State("Admin"))), AppPermission.ManageJobs));
    }

    // ---------- Trang: AuthorizeRouteView ----------

    private static readonly Type[] PublicPages = [typeof(Login), typeof(NotFound), typeof(Error)];
    private static readonly Type[] AdminPages = [typeof(JobEdit)];

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
            var expected = PublicPages.Contains(page) || (AdminPages.Contains(page) ? state == "Admin" : business);
            Assert.True(expected == await CanOpen(page, user), $"{page.Name} / {state}: mong đợi {(expected ? "MỞ" : "CHẶN")}");
        }
    }

    [Fact]
    public void Job_editor_routes_are_admin_only_but_the_job_list_is_not()
    {
        var routes = typeof(JobEdit).GetCustomAttributes<RouteAttribute>().Select(r => r.Template).ToList();
        Assert.Equal(["/jobs/new", "/jobs/{Id:int}"], routes);
        Assert.Equal(AccessRules.AdminPolicy, Assert.Single(typeof(JobEdit).GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy is not null).Policy);
        Assert.DoesNotContain(typeof(JobsPage).GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy is not null || a.Roles is not null);
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

    [Theory]
    [InlineData("HR")]
    [InlineData("Admin")]
    public void Menu_shows_only_reachable_pages_and_never_an_empty_group(string role)
    {
        var user = B28Auth.Principal(role);
        var groups = AppNavigation.For(user);
        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.NotEmpty(g.Items));
        if (role == "HR") Assert.All(groups.SelectMany(g => g.Items), i => Assert.False(i.AdminOnly));
        // Mỗi mục trỏ tới một trang mà vai trò này mở được.
        var pages = RoutablePages().SelectMany(p => p.GetCustomAttributes<RouteAttribute>().Select(r => (Page: p, Path: r.Template.Trim('/')))).ToList();
        foreach (var item in groups.SelectMany(g => g.Items))
        {
            var target = pages.FirstOrDefault(p => p.Path.Equals(item.Href, StringComparison.OrdinalIgnoreCase));
            Assert.True(target.Page is not null, $"Mục menu {item.Text} trỏ tới đường dẫn chết /{item.Href}");
            Assert.True(CanOpen(target.Page, user).GetAwaiter().GetResult(), $"{role} thấy {item.Text} nhưng không mở được");
        }
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("unverified")]
    [InlineData("mcp")]
    [InlineData("norole")]
    public void Menu_is_empty_for_anyone_who_cannot_use_the_app(string state) => Assert.Empty(AppNavigation.For(State(state)));

    [Fact]
    public void Menu_keeps_the_existing_eight_destinations_in_order()
    {
        var hrefs = AppNavigation.For(B28Auth.Principal("HR")).SelectMany(g => g.Items).Select(i => i.Href);
        Assert.Equal(["", "upload", "candidates", "results", "prompts", "evaluation-sets", "jobs", "runs"], hrefs);
    }

    [Theory]
    [InlineData("jobs/new", "jobs")]
    [InlineData("jobs/12?x=1", "jobs")]
    [InlineData("jobs", null)]
    [InlineData("forbidden", null)]
    [InlineData("unknown/thing", null)]
    [InlineData("", null)]
    public void Forbidden_page_back_link_is_an_in_app_parent(string path, string? expected) =>
        Assert.Equal(expected, AppNavigation.ParentFor(path, B28Auth.Principal("HR"))?.Href);
}
