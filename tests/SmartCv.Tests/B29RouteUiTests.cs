extern alias web;

using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components;
using web::SmartCv.Web.Services;
using Nav = SmartCv.Tests.B28AuthStateProviderTests.B28Nav;

namespace SmartCv.Tests;

/// <summary>
/// B29: vẽ thật Routes.razor (AuthorizeRouteView + MainLayout + AuthGate) cho /users và /change-password với AuthSession và DI
/// phân quyền thật của Web; API là máy chủ giả ghi lại mọi request.
/// </summary>
public class B29RouteUiTests
{
    private sealed class Js : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    private sealed class NoInterception : INavigationInterception { public Task EnableNavigationInterceptionAsync() => Task.CompletedTask; }
    private sealed class NoScroll : IScrollToLocationHash { public Task RefreshScrollPositionForHash(string locationAbsolute) => Task.CompletedTask; }

    private static B27CaptureHandler Api() => new()
    {
        Respond = r => Task.FromResult(r.RequestUri!.AbsolutePath switch
        {
            "/api/health" => B27CaptureHandler.Json(HttpStatusCode.OK, new { status = "ok", time = DateTime.UtcNow }),
            "/api/users" => B29UsersTests.Paged(r,
            [
                B29UsersTests.Dto(7, "Nguyễn Thị Lan", role: "Admin"),
                B29UsersTests.Dto(8, "Trần Minh Khoa"),
                B29UsersTests.Dto(9, "Phạm Quốc Bảo", active: false),
            ], 3, admins: 1),
            _ => B27CaptureHandler.Json(HttpStatusCode.OK, new { })
        })
    };

    private static ServiceProvider Services(AuthSession session, Nav nav, B27CaptureHandler api)
    {
        var http = new HttpClient(api) { BaseAddress = new Uri("https://api.test/") };
        var services = new ServiceCollection().AddLogging()
            .AddSingleton(session).AddSingleton<IAuthApi>(new B27FakeAuthApi()).AddSingleton<NavigationManager>(nav)
            .AddSingleton(TimeProvider.System).AddSingleton<IJSRuntime>(new Js())
            .AddSingleton<INavigationInterception>(new NoInterception()).AddSingleton<IScrollToLocationHash>(new NoScroll())
            .AddSingleton(new HealthApiClient(http, NullLogger<HealthApiClient>.Instance))
            .AddSingleton(new UserApiClient(http, NullLogger<UserApiClient>.Instance));
        services.AddSupplyValueFromQueryProvider();
        services.AddSmartCvWebAuth();
        return services.BuildServiceProvider();
    }

    private static async Task<AuthSession> As(string state)
    {
        var session = B27.Session();
        if (state == "anonymous") return session;
        // B27.User: id 7, "Nguyễn Thị Lan" — cùng mã với dòng Admin trong danh sách giả ("Bạn").
        await session.SignInAsync(B27.Login("tok", state == "mcp" ? "HR" : state, mustChange: state == "mcp"));
        return session;
    }

    private static async Task<(string Html, Nav Nav, B27CaptureHandler Api)> Open(string state, string path)
    {
        var nav = new Nav("https://app.test/" + path);
        var api = Api();
        using var sp = Services(await As(state), nav, api);
        await using var renderer = new HtmlRenderer(sp, sp.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<Routes>()).ToHtmlString()));
        return (System.Text.RegularExpressions.Regex.Replace(html, @" b-[a-z0-9]{10}", ""), nav, api);
    }

    private static IEnumerable<string> BusinessCalls(B27CaptureHandler api) =>
        api.Calls.Select(c => c.PathAndQuery).Where(c => !c.StartsWith("/api/health"));

    [Fact]
    public async Task Admin_sees_the_account_console_with_server_paging_and_row_states()
    {
        var (html, nav, api) = await Open("Admin", "users");
        Assert.Contains("Quản lý tài khoản", html);
        Assert.Contains("Tạo tài khoản", html);
        Assert.Contains("Trần Minh Khoa", html);
        Assert.Contains(">Bạn</span>", html);                      // dòng của chính mình
        Assert.Contains("Đã khóa", html);
        Assert.Contains("Hiển thị <strong>1–3</strong> / 3 tài khoản", html);
        Assert.Contains("<span>Quản lý tài khoản</span>", html);   // mục menu chỉ Admin
        Assert.Matches("<a[^>]*href=\"users\"[^>]*class=\"nav-item active\"|<a[^>]*class=\"nav-item active\"[^>]*href=\"users\"", html);
        Assert.Contains("<span>Audit Log</span>", html);
        Assert.Contains("/api/users?page=1&pageSize=20", BusinessCalls(api));
        Assert.Contains("/api/users?role=Admin&isActive=true&page=1&pageSize=1", BusinessCalls(api));
        Assert.Empty(nav.Navigations);
    }

    [Fact]
    public async Task Hr_gets_403_for_users_with_no_request_and_no_menu_entry()
    {
        var (html, nav, api) = await Open("HR", "users");
        Assert.Contains("Bạn không có quyền truy cập trang này", html);
        Assert.DoesNotContain("<span>Quản lý tài khoản</span>", html);
        Assert.DoesNotContain("href=\"users\"", html);
        Assert.Empty(BusinessCalls(api));
        Assert.Empty(nav.Navigations);
    }

    [Theory]
    [InlineData("users", "login?returnUrl=%2Fusers")]
    [InlineData("change-password", "login?returnUrl=%2Fchange-password")]
    public async Task Anonymous_is_sent_to_login(string path, string expected)
    {
        var (_, nav, api) = await Open("anonymous", path);
        Assert.Equal((expected, false, true), Assert.Single(nav.Navigations));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Temporary_password_account_opens_change_password_without_a_loop_menu_or_business_calls()
    {
        var (html, nav, api) = await Open("mcp", "change-password?returnUrl=%2Fjobs");
        Assert.Contains("Đặt mật khẩu mới", html);
        Assert.Contains("Bước bắt buộc", html);
        Assert.Contains("Mật khẩu hiện tại", html);
        Assert.Contains("autocomplete=\"new-password\"", html);
        Assert.Contains("Đăng xuất", html);
        Assert.DoesNotContain("Hủy và quay lại", html);            // không bỏ qua được
        Assert.DoesNotContain("main-navigation", html);
        Assert.Empty(nav.Navigations);                              // không vòng lặp chuyển hướng
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Temporary_password_account_is_sent_from_users_to_change_password_not_403()
    {
        var (html, nav, api) = await Open("mcp", "users");
        Assert.DoesNotContain("Bạn không có quyền truy cập", html);
        Assert.Equal(("change-password?returnUrl=%2Fusers", false, true), Assert.Single(nav.Navigations));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Signed_in_user_changes_password_voluntarily_inside_the_app()
    {
        var (html, nav, _) = await Open("HR", "change-password?returnUrl=%2Fcandidates");
        Assert.Contains("<h1 id=\"cp-title\" tabindex=\"-1\">Đổi mật khẩu</h1>", html);
        Assert.Contains("main-navigation", html);
        Assert.Contains("href=\"/candidates\"", html);              // Hủy quay về trang trước (an toàn)
        Assert.DoesNotContain("Bước bắt buộc", html);
        Assert.Empty(nav.Navigations);
    }

    [Fact]
    public async Task Topbar_offers_change_password_to_every_signed_in_user()
    {
        var (html, _, _) = await Open("HR", "forbidden");
        Assert.Contains("aria-label=\"Đổi mật khẩu\"", html);
    }
}
