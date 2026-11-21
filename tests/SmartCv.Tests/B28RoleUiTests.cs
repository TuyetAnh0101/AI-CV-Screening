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
using web::SmartCv.Web.Components.Layout;
using web::SmartCv.Web.Components.Prompts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
using Nav = SmartCv.Tests.B28AuthStateProviderTests.B28Nav;

namespace SmartCv.Tests;

/// <summary>
/// B28: vẽ thật Routes.razor (Router + AuthorizeRouteView + MainLayout + AuthGate) bằng HtmlRenderer với AuthSession và
/// DI phân quyền thật của Web; API là máy chủ giả ghi lại mọi request. Không trình duyệt, không database.
/// </summary>
public class B28RoleUiTests
{
    private sealed class Js : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private sealed class NoInterception : INavigationInterception
    {
        public Task EnableNavigationInterceptionAsync() => Task.CompletedTask;
    }

    private sealed class NoScroll : IScrollToLocationHash
    {
        public Task RefreshScrollPositionForHash(string locationAbsolute) => Task.CompletedTask;
    }

    private static readonly JobDto Job = new()
    {
        JobId = 3, Title = "Backend .NET Intern", IsActive = true, MustHaveCount = 1, HasEvaluations = false, ApplicationCount = 2,
        StageCounts = new() { ["New"] = 2 }, Skills = [new JobSkillDto { SkillCode = "csharp", DisplayName = "C#", SkillType = "MustHave" }]
    };

    private static B27CaptureHandler Api() => new()
    {
        Respond = request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/health" => B27CaptureHandler.Json(HttpStatusCode.OK, new { status = "ok", time = DateTime.UtcNow }),
            "/api/jobs" => B27CaptureHandler.Json(HttpStatusCode.OK, new[] { Job }),
            "/api/jobs/3" => B27CaptureHandler.Json(HttpStatusCode.OK, Job),
            "/api/evaluation-sets" => B27CaptureHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
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
            .AddSingleton(new JobApiClient(http, NullLogger<JobApiClient>.Instance))
            .AddSingleton(new DashboardApiClient(http, NullLogger<DashboardApiClient>.Instance))
            .AddSingleton(new PromptApiClient(http, NullLogger<PromptApiClient>.Instance));
        services.AddSupplyValueFromQueryProvider();
        services.AddSmartCvWebAuth();   // đúng chính sách, provider, navigator của Web
        return services.BuildServiceProvider();
    }

    private static async Task<string> Html<T>(ServiceProvider services, Dictionary<string, object?>? parameters = null) where T : IComponent
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            WebUtility.HtmlDecode((await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters ?? []))).ToHtmlString()));
    }

    private static async Task<AuthSession> As(string state)
    {
        switch (state)
        {
            case "anonymous": return B27.Session();
            case "unverified":
                var store = new B27MemoryStore { Saved = new("saved", DateTime.UtcNow.AddHours(2), B27.User("Admin")) };
                return B27.Session(store, new B27FakeAuthApi { Me = _ => ApiResult<MeDto>.LocalFailure("offline", ApiFailureKind.Network) });
            default:
                var session = B27.Session();
                await session.SignInAsync(state == "mcp" ? B27.Login("tok", "HR", mustChange: true) : B27.Login("tok", state == "norole" ? "Manager" : state));
                return session;
        }
    }

    private static async Task<(string Html, Nav Nav, B27CaptureHandler Api)> Open(string state, string path)
    {
        var nav = new Nav("https://app.test/" + path);
        var api = Api();
        using var sp = Services(await As(state), nav, api);
        return (await Html<Routes>(sp), nav, api);
    }

    private static IEnumerable<string> BusinessCalls(B27CaptureHandler api) =>
        api.Calls.Select(c => c.Method + " " + c.PathAndQuery).Where(c => !c.EndsWith("/api/health"));

    // ---------- Chặn trang ----------

    [Fact]
    public async Task Hr_opening_either_job_editor_route_loads_the_business_form()
    {
        foreach (var path in new[] { "jobs/new", "jobs/3" })
        {
            var (html, nav, api) = await Open("HR", path);
            Assert.DoesNotContain("Bạn không có quyền truy cập trang này", html);
            Assert.Contains("main-navigation", html);
            Assert.Contains("id=\"job-title\"", html);
            Assert.Contains(path.EndsWith("new") ? "Tạo vị trí tuyển dụng" : "Chỉnh sửa vị trí", html);
            if (path == "jobs/3") Assert.Contains("GET /api/jobs/3", BusinessCalls(api));
            else Assert.Empty(BusinessCalls(api));
            Assert.Empty(nav.Navigations);
        }
    }

    [Fact]
    public async Task Admin_opens_the_job_form()
    {
        var (html, nav, _) = await Open("Admin", "jobs/new");
        Assert.Contains("Tạo vị trí tuyển dụng", html);
        Assert.DoesNotContain("Bạn không có quyền truy cập", html);
        Assert.Empty(nav.Navigations);
    }

    [Theory]
    [InlineData("jobs", "login?returnUrl=%2Fjobs")]
    [InlineData("jobs/new", "login?returnUrl=%2Fjobs%2Fnew")]
    [InlineData("forbidden", "login?returnUrl=%2Fforbidden")]
    [InlineData("dashboard", "login?returnUrl=%2Fdashboard")]
    public async Task Anonymous_visitor_is_sent_to_login_not_to_403_and_nothing_loads(string path, string expected)
    {
        var (html, nav, api) = await Open("anonymous", path);
        Assert.Equal((expected, false, true), Assert.Single(nav.Navigations));
        Assert.DoesNotContain("main-navigation", html);
        Assert.DoesNotContain("Bạn không có quyền", html);
        Assert.Empty(api.Calls);
    }

    // ---------- B51: Landing "/" công khai, Dashboard ở /dashboard ----------

    [Fact]
    public async Task B51_anonymous_root_shows_landing_without_menu_redirect_or_api_calls()
    {
        var (html, nav, api) = await Open("anonymous", "");
        Assert.Contains("href=\"/login\"", html);
        Assert.Contains("id=\"main-content\"", html);
        Assert.DoesNotContain("main-navigation", html);
        Assert.DoesNotContain("Vào bảng điều khiển", html);
        Assert.Empty(nav.Navigations);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task B51_signed_in_root_shows_landing_with_dashboard_button_and_no_auto_redirect()
    {
        var (html, nav, api) = await Open("HR", "");
        Assert.Contains("Vào bảng điều khiển", html);
        Assert.Contains("href=\"/dashboard\"", html);
        Assert.DoesNotContain("main-navigation", html);   // không dùng MainLayout
        Assert.Empty(nav.Navigations);
        Assert.Empty(BusinessCalls(api));
    }

    [Theory]
    [InlineData("?jobId=3", "/dashboard?jobId=3")]
    [InlineData("?from=2026-11-01&to=2026-11-10", "/dashboard?from=2026-11-01&to=2026-11-10")]
    public async Task B51_signed_in_root_with_dashboard_query_moves_to_dashboard(string query, string expected)
    {
        var (_, nav, _) = await Open("HR", query);
        Assert.Equal((expected, false, true), Assert.Single(nav.Navigations));
    }

    [Theory]
    [InlineData("HR", "?utm_source=mail")]
    [InlineData("HR", "?returnUrl=%2Fjobs")]
    [InlineData("anonymous", "?jobId=3")]
    public async Task B51_root_keeps_landing_for_foreign_query_or_anonymous(string state, string query)
    {
        var (html, nav, api) = await Open(state, query);
        Assert.Empty(nav.Navigations);
        Assert.Contains("id=\"main-content\"", html);
        Assert.Empty(BusinessCalls(api));
    }

    [Fact]
    public async Task B51_admin_opening_dashboard_goes_to_admin_dashboard_once()
    {
        var (_, nav, _) = await Open("Admin", "dashboard?jobId=3");
        Assert.Equal(("/admin/dashboard", false, true), Assert.Single(nav.Navigations));
        var (_, recruitmentNav, _) = await Open("Admin", "recruitment");
        Assert.Empty(recruitmentNav.Navigations);
    }

    [Fact]
    public async Task B51_hr_opening_dashboard_stays_and_does_not_redirect()
    {
        var (html, nav, _) = await Open("HR", "dashboard");
        Assert.Contains("main-navigation", html);
        Assert.Contains("href=\"/dashboard\"", html);   // logo + menu
        Assert.Empty(nav.Navigations);
    }

    [Fact]
    public async Task Unverified_session_waits_for_retry_without_menu_data_or_redirect()
    {
        var (html, nav, api) = await Open("unverified", "candidates");
        Assert.Contains("Chưa xác minh được phiên đăng nhập", html);
        Assert.Contains("Thử lại", html);
        Assert.Contains("Đăng xuất", html);
        Assert.DoesNotContain("main-navigation", html);
        Assert.DoesNotContain("Email hoặc mật khẩu", html);   // không báo sai là mật khẩu không đúng
        Assert.Empty(nav.Navigations);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Password_change_required_goes_to_change_password_not_403_and_no_menu()
    {
        // B29: thay hướng dẫn của B28 bằng chuyển tới /change-password (giữ trang muốn mở).
        var (html, nav, api) = await Open("mcp", "jobs");
        Assert.DoesNotContain("Bạn không có quyền truy cập", html);
        Assert.DoesNotContain("main-navigation", html);
        Assert.Equal(("change-password?returnUrl=%2Fjobs", false, true), Assert.Single(nav.Navigations));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Unknown_role_gets_403_with_sign_out_and_no_menu()
    {
        var (html, _, api) = await Open("norole", "jobs");
        Assert.Contains("Bạn không có quyền truy cập trang này", html);
        Assert.Contains("Đăng xuất", html);
        Assert.DoesNotContain("Về Tổng quan", html);
        Assert.DoesNotContain("main-navigation", html);
        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData("anonymous", "Đăng nhập")]
    [InlineData("HR", "Về bảng điều khiển")]
    public async Task Unknown_address_is_not_found_whether_signed_in_or_not(string state, string action)
    {
        var (html, nav, _) = await Open(state, "khong-co-trang-nay");
        Assert.Contains("Không tìm thấy trang", html);
        Assert.Contains(action, html);
        Assert.Empty(nav.Navigations);
    }

    // ---------- Thao tác theo vai trò ----------

    [Fact]
    public async Task Hr_sees_the_job_list_and_business_actions()
    {
        var (html, _, api) = await Open("HR", "jobs");
        Assert.Contains("Backend .NET Intern", html);
        Assert.DoesNotContain("id=\"jobs-readonly-note\"", html);
        Assert.Contains("Tạo vị trí", html);
        Assert.Contains("Sửa vị trí", html);
        Assert.Contains("Ngừng tuyển Backend", html);
        Assert.Contains("href=\"jobs/3\"", html);
        Assert.All(BusinessCalls(api), c => Assert.StartsWith("GET ", c));
    }

    [Fact]
    public async Task Admin_sees_the_job_actions()
    {
        var (html, _, _) = await Open("Admin", "jobs");
        Assert.Contains("Tạo vị trí", html);
        Assert.Contains("Sửa vị trí", html);
        Assert.Contains("Ngừng tuyển", html);
        Assert.Contains("href=\"jobs/3\"", html);
        Assert.DoesNotContain("Bạn có quyền xem danh sách", html);
    }

    private static readonly List<PromptVersionDto> Versions =
    [
        new() { PromptVersionId = 2, VersionName = "v2", SystemPrompt = "Bạn là chuyên gia.", IsActive = true, CreatedAt = DateTime.UtcNow },
        new() { PromptVersionId = 1, VersionName = "v1", SystemPrompt = "Cũ.", IsActive = false, CreatedAt = DateTime.UtcNow.AddDays(-1) },
    ];

    private static ServiceProvider ComponentServices(string role, B27CaptureHandler? api = null)
    {
        var http = new HttpClient(api ?? Api()) { BaseAddress = new Uri("https://api.test/") };
        return new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new Js()).AddSingleton<NavigationManager>(new Nav())
            .AddSingleton(new PromptApiClient(http, NullLogger<PromptApiClient>.Instance))
            .AddSingleton(new AiPreviewApiClient(http, NullLogger<AiPreviewApiClient>.Instance))
            .AddSingleton(new AnonymityState())
            .AddB28Auth(role).BuildServiceProvider();
    }

    [Theory]
    [InlineData("HR", true)]
    [InlineData("Admin", true)]
    public async Task Prompt_history_shows_activate_to_both_business_roles(string role, bool visible)
    {
        using var sp = ComponentServices(role);
        var html = await Html<PromptHistory>(sp, new() { ["Versions"] = Versions, ["SelectedId"] = 2 });
        Assert.Contains("v1", html);
        Assert.Equal(visible, html.Contains("Kích hoạt v1"));
    }

    [Theory]
    [InlineData("HR", true)]
    [InlineData("Admin", true)]
    public async Task Both_business_roles_can_edit_save_and_try_a_prompt(string role, bool canSave)
    {
        using var sp = ComponentServices(role);
        var draft = new PromptDraft();
        draft.Load(Versions[0]);
        var html = await Html<PromptEditor>(sp, new() { ["Draft"] = draft, ["Versions"] = Versions });
        Assert.Contains("Bạn là chuyên gia.", html);
        Assert.Contains("Thử với hồ sơ", html);
        Assert.Equal(canSave, html.Contains("Lưu thành phiên bản mới"));
        Assert.Equal(canSave, html.Contains("Tên phiên bản mới"));
        Assert.Equal(!canSave, html.Contains("Chế độ chỉ xem"));
        Assert.Equal(!canSave, html.Contains(" readonly"));
    }

    [Theory]
    [InlineData("HR")]
    [InlineData("Admin")]
    public async Task Both_business_roles_see_the_masked_payload_tab_in_the_playground(string role)
    {
        using var sp = ComponentServices(role);
        var draft = new PromptDraft(); draft.Load(Versions[0]);
        var html = await Html<PromptPlayground>(sp, new() { ["Draft"] = draft });
        Assert.Contains("Nội dung gửi Gemini", html);
        Assert.Contains("aria-controls=\"pg-panel-payload\"", html);
    }

    [Theory]
    [InlineData("HR")]
    [InlineData("Admin")]
    public async Task Both_business_roles_can_create_an_evaluation_set(string role)
    {
        var api = Api();
        using var sp = ComponentServices(role, api);
        var html = await Html<EvaluationSetManager>(sp);
        Assert.Contains("Tên bộ CV mới", html);
        Assert.Contains("id=\"create-set-open\"", html);
        Assert.Contains("id=\"create-set-submit\"", html);
        Assert.DoesNotContain("Quản trị viên (Admin) tạo bộ", html);
        Assert.All(BusinessCalls(api), c => Assert.StartsWith("GET ", c));
    }

    // ---------- Menu ----------

    [Fact]
    public async Task Hr_menu_has_every_reachable_page_and_a_mobile_account_block_with_role_and_logout()
    {
        var (raw, _, _) = await Open("HR", "forbidden");   // trang không cần API nào: chỉ kiểm tra khung + menu
        var html = System.Text.RegularExpressions.Regex.Replace(raw, @" b-[a-z0-9]{10}", "");
        foreach (var item in new[] { "Dashboard tuyển dụng", "Tải hồ sơ lên", "Ứng viên", "Kết quả", "System prompt", "Bộ dữ liệu đánh giá", "Vị trí tuyển dụng", "Lịch sử lượt chấm" })
            Assert.Contains($"<span>{item}</span>", html);
        Assert.Contains("nav-account", html);
        Assert.Contains("nav-account-role", html);
        Assert.Contains("aria-label=\"Mở menu\"", html);
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "class=\"nav-group\"").Count);
    }

    [Fact]
    public async Task Disposed_gate_no_longer_reacts_to_a_401()
    {
        await using var tab = await B28Tab.SignedInAsync("HR");
        var nav = new Nav("https://app.test/forbidden");
        using (var sp = Services(tab.Session, nav, Api()))
            await Html<Routes>(sp);          // renderer bị hủy sau khi vẽ: AuthGate.Dispose phải gỡ đăng ký Expired
        var expired = 0; tab.Session.Expired += () => expired++;
        await tab.RevokeAsync();
        Assert.Equal(1, expired);             // phiên đúng là đã kết thúc...
        Assert.Empty(nav.Navigations);        // ...nhưng component đã hủy không còn phản ứng
    }
}
