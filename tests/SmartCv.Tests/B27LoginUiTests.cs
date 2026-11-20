extern alias web;

using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using web::SmartCv.Web.Components.Layout;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>B27: trang đăng nhập, chốt AuthGate và khung ứng dụng — vẽ thật bằng HtmlRenderer (không cần trình duyệt).</summary>
public class B27LoginUiTests
{
    private sealed class Nav : NavigationManager
    {
        public List<(string Uri, bool ForceLoad, bool Replace)> Navigations { get; } = [];
        public Nav(string uri = "https://app.test/") => Initialize("https://app.test/", uri);
        protected override void NavigateToCore(string uri, bool forceLoad) => NavigateToCore(uri, new NavigationOptions { ForceLoad = forceLoad });
        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Navigations.Add((uri, options.ForceLoad, options.ReplaceHistoryEntry));
            Uri = ToAbsoluteUri(uri).AbsoluteUri;
        }
    }

    private sealed class Js : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private static ServiceProvider Services(AuthSession session, Nav nav, B27CaptureHandler? api = null)
    {
        api ??= new B27CaptureHandler { Respond = _ => Task.FromResult(B27CaptureHandler.Json(HttpStatusCode.OK, new { status = "ok", time = DateTime.UtcNow })) };
        var services = new ServiceCollection().AddLogging()
            .AddSingleton(session).AddSingleton<IAuthApi>(new B27FakeAuthApi()).AddSingleton<NavigationManager>(nav)
            .AddSingleton(TimeProvider.System).AddSingleton<IJSRuntime>(new Js())
            .AddSingleton(new HealthApiClient(new HttpClient(api) { BaseAddress = new Uri("https://api.test/") }, NullLogger<HealthApiClient>.Instance));
        services.AddSupplyValueFromQueryProvider();
        services.AddSmartCvWebAuth();   // B28: trạng thái đăng nhập cascade đọc từ đúng AuthSession ở trên
        return services.BuildServiceProvider();
    }

    private static async Task<string> Html<T>(ServiceProvider services, Dictionary<string, object?>? parameters = null) where T : IComponent
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            WebUtility.HtmlDecode((await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters ?? []))).ToHtmlString()));
    }

    private static RenderFragment Text(string text) => b => b.AddContent(0, text);

    /// <summary>Bỏ thuộc tính CSS isolation (b-xxxxxxxxxx) để so sánh thẻ HTML.</summary>
    private static string Unscoped(string html) => System.Text.RegularExpressions.Regex.Replace(html, @" b-[a-z0-9]{10}", "");

    private static async Task<AuthSession> SignedIn(string role = "Admin", bool mustChange = false)
    {
        var session = B27.Session();
        await session.SignInAsync(B27.Login("tok", role, mustChange));
        return session;
    }

    [Fact]
    public async Task Login_form_has_labelled_fields_autocomplete_and_a_password_toggle()
    {
        using var sp = Services(B27.Session(), new Nav("https://app.test/login"));
        var html = Unscoped(await Html<Login>(sp));
        Assert.Contains("<h1 id=\"login-title\">Đăng nhập</h1>", html);
        Assert.Contains("<label class=\"field-label\" for=\"login-email\">Email</label>", html);
        Assert.Contains("<label class=\"field-label\" for=\"login-password\">Mật khẩu</label>", html);
        Assert.Contains("id=\"login-email\"", html); Assert.Contains("autocomplete=\"username\"", html); Assert.Contains("type=\"email\"", html);
        Assert.Contains("id=\"login-password\"", html); Assert.Contains("autocomplete=\"current-password\"", html); Assert.Contains("type=\"password\"", html);
        Assert.Contains("aria-controls=\"login-password\" aria-pressed=\"false\" aria-label=\"Hiện mật khẩu\"", html);
        Assert.Contains("<button type=\"submit\"", html);                      // Enter gửi form
        Assert.Contains("novalidate", html);
        Assert.Contains("Liên hệ <strong>quản trị viên (Admin)</strong>", html);
        Assert.DoesNotContain("Quên mật khẩu?</a>", html);                     // không có link tới trang không tồn tại
        Assert.DoesNotContain("Tạo tài khoản", html);
        Assert.DoesNotContain("main-navigation", html);
    }

    [Fact]
    public async Task Login_uses_the_auth_layout_which_has_no_application_menu()
    {
        Assert.Equal(typeof(AuthLayout), typeof(Login).GetCustomAttribute<LayoutAttribute>()!.LayoutType);
        using var sp = Services(B27.Session(), new Nav("https://app.test/login"));
        var html = Unscoped(await Html<AuthLayout>(sp, new() { ["Body"] = Text("FORM") }));
        Assert.Contains("FORM", html);
        Assert.Contains("SC<span>App</span>", html);
        Assert.DoesNotContain("main-navigation", html);
        Assert.DoesNotContain("topbar", html);
        Assert.DoesNotContain("Đăng xuất", html);
    }

    [Theory]
    [InlineData("https://app.test/login?expired=1", AuthText.SessionExpired)]
    [InlineData("https://app.test/login?signedOut=1", AuthText.SignedOut)]
    public async Task Login_explains_why_the_user_is_back(string uri, string expected)
    {
        using var sp = Services(B27.Session(), new Nav(uri));
        Assert.Contains(expected, await Html<Login>(sp));
    }

    [Theory]
    [InlineData("https://app.test/login?returnUrl=%2Fcvs%3Fsearch%3Djava", "/cvs?search=java")]
    [InlineData("https://app.test/login?returnUrl=https%3A%2F%2Fgoogle.com", "/")]
    [InlineData("https://app.test/login?returnUrl=%2Flogin", "/")]
    public async Task Signed_in_user_opening_login_goes_back_to_a_safe_page(string uri, string expected)
    {
        var nav = new Nav(uri);
        using var sp = Services(await SignedIn(), nav);
        var html = await Html<Login>(sp);
        Assert.Equal((expected, false, true), Assert.Single(nav.Navigations));
        Assert.DoesNotContain("login-form", html);
    }

    // B28: trang nào được mở do AuthorizeRouteView quyết định; khi bị chặn, Body của layout là NotAuthorizedContent
    // (không phải trang). Các test chốt của B27 dưới đây giữ nguyên tiêu chí nhưng dựng đúng như Routes.razor làm.
    private static async Task<RenderFragment> DeniedBody(ServiceProvider sp)
    {
        var state = await sp.GetRequiredService<ApiAuthenticationStateProvider>().GetAuthenticationStateAsync();
        return b => { b.OpenComponent<web::SmartCv.Web.Components.Shared.NotAuthorizedContent>(0); b.AddComponentParameter(1, "User", state.User); b.CloseComponent(); };
    }

    [Fact]
    public async Task Gate_sends_a_signed_out_visitor_to_login_with_the_current_page_and_shows_nothing()
    {
        var nav = new Nav("https://app.test/cvs?search=java");
        using var sp = Services(B27.Session(), nav);
        var html = await Html<MainLayout>(sp, new() { ["Body"] = await DeniedBody(sp) });
        Assert.DoesNotContain("main-navigation", html);
        Assert.Contains("Đang chuyển tới trang đăng nhập", html);
        Assert.Equal(("login?returnUrl=%2Fcvs%3Fsearch%3Djava", false, true), Assert.Single(nav.Navigations));
    }

    [Fact]
    public async Task Gate_marks_expiry_when_the_saved_session_was_revoked()
    {
        var store = new B27MemoryStore { Saved = new("old", DateTime.UtcNow.AddHours(1), B27.User()) };
        var api = new B27FakeAuthApi { Me = _ => web::SmartCv.Web.Services.ApiResult<SmartCv.Core.Contracts.MeDto>.Failure("x", 401) };
        var nav = new Nav("https://app.test/runs");
        using var sp = Services(B27.Session(store, api), nav);
        await Html<MainLayout>(sp, new() { ["Body"] = await DeniedBody(sp) });
        Assert.Equal(("login?returnUrl=%2Fruns&expired=1", false, true), Assert.Single(nav.Navigations));
    }

    [Fact]
    public async Task Signed_out_shell_renders_no_menu_no_page_and_calls_no_API()
    {
        var api = new B27CaptureHandler();
        var nav = new Nav("https://app.test/");
        using var sp = Services(B27.Session(), nav, api);
        var html = await Html<MainLayout>(sp, new() { ["Body"] = await DeniedBody(sp) });
        Assert.DoesNotContain("DASHBOARD-DATA", html);
        Assert.DoesNotContain("main-navigation", html);
        Assert.DoesNotContain("Đăng xuất", html);
        Assert.Empty(api.Calls);
        Assert.Equal("login", Assert.Single(nav.Navigations).Uri);
    }

    [Fact]
    public async Task Signed_in_shell_shows_the_user_role_and_a_logout_button()
    {
        var nav = new Nav("https://app.test/");
        using var sp = Services(await SignedIn("HR"), nav);
        var html = await Html<MainLayout>(sp, new() { ["Body"] = Text("DASHBOARD-DATA") });
        Assert.Contains("DASHBOARD-DATA", html);
        Assert.Contains("main-navigation", html);
        Assert.Contains(">Nguyễn Thị Lan</span>", html);
        Assert.Contains(">NL</span>", html);
        Assert.Contains("Nhân sự (HR)", html);
        Assert.Contains("aria-label=\"Đăng xuất\"", html);
        Assert.Empty(nav.Navigations);
    }

    [Fact]
    public async Task Must_change_password_account_sees_guidance_instead_of_failing_pages()
    {
        var nav = new Nav("https://app.test/");
        using var sp = Services(await SignedIn("HR", mustChange: true), nav);
        var html = await Html<MainLayout>(sp, new() { ["Body"] = await DeniedBody(sp) });
        Assert.Contains("Cần đổi mật khẩu trước khi tiếp tục", html);
        Assert.Contains("Đăng xuất", html);
        Assert.DoesNotContain("DASHBOARD-DATA", html);
        Assert.DoesNotContain("main-navigation", html);
        Assert.Empty(nav.Navigations);
    }

    [Fact]
    public void App_disables_prerender_so_pages_never_run_before_the_session_is_known()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SmartCv.Web", "Components", "App.razor"));
        var app = File.ReadAllText(root);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(app, @"new InteractiveServerRenderMode\(prerender: false\)").Count);
        Assert.DoesNotContain("@rendermode=\"InteractiveServer\"", app);
    }
}
