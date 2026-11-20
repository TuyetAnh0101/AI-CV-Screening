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
    public async Task Hr_opening_the_job_form_by_url_sees_403_inside_the_app_and_the_form_never_loads()
    {
        foreach (var path in new[] { "jobs/new", "jobs/3" })
        {
            var (html, nav, api) = await Open("HR", path);
            Assert.Contains("Bạn không có quyền truy cập trang này", html);
            Assert.Contains("Tài khoản hiện tại chưa được cấp quyền sử dụng chức năng này", html);
            Assert.Contains("Nhân sự (HR)", html);
            Assert.Contains("Về Tổng quan", html);
            Assert.Contains("Mở Vị trí tuyển dụng", html);           // quay lại an toàn trong ứng dụng
            Assert.Contains("main-navigation", html);                 // vẫn ở trong ứng dụng, có menu
            Assert.DoesNotContain("Tạo vị trí tuyển dụng", html);     // tiêu đề form không có
            Assert.DoesNotContain("Policy", html); Assert.DoesNotContain("SmartCv.Admin", html); Assert.DoesNotContain("/api/", html);
            Assert.Empty(BusinessCalls(api));                          // JobEdit không được tạo → không GET /api/jobs/3
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
    [InlineData("", "login")]
    public async Task Anonymous_visitor_is_sent_to_login_not_to_403_and_nothing_loads(string path, string expected)
    {
        var (html, nav, api) = await Open("anonymous", path);
        Assert.Equal((expected, false, true), Assert.Single(nav.Navigations));
        Assert.DoesNotContain("main-navigation", html);
        Assert.DoesNotContain("Bạn không có quyền", html);
        Assert.Empty(api.Calls);
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
    public async Task Password_change_required_shows_guidance_not_403_and_no_menu()
    {
        var (html, nav, api) = await Open("mcp", "jobs");
        Assert.Contains("Cần đổi mật khẩu trước khi tiếp tục", html);
        Assert.DoesNotContain("Bạn không có quyền truy cập", html);
        Assert.DoesNotContain("main-navigation", html);
        Assert.Empty(nav.Navigations);
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
    [InlineData("HR", "Về Tổng quan")]
    public async Task Unknown_address_is_not_found_whether_signed_in_or_not(string state, string action)
    {
        var (html, nav, _) = await Open(state, "khong-co-trang-nay");
        Assert.Contains("Không tìm thấy trang", html);
        Assert.Contains(action, html);
        Assert.Empty(nav.Navigations);
    }

    // ---------- Thao tác theo vai trò ----------

    [Fact]
    public async Task Hr_sees_the_job_list_but_no_admin_actions()
    {
        var (html, _, api) = await Open("HR", "jobs");
        Assert.Contains("Backend .NET Intern", html);
        Assert.Contains("Bạn có quyền xem danh sách", html);
        Assert.DoesNotContain("Tạo vị trí", html);
        Assert.DoesNotContain("Sửa vị trí", html);
        Assert.DoesNotContain("Ngừng tuyển Backend", html);
        Assert.DoesNotContain("href=\"jobs/3\"", html);              // tên vị trí không dẫn tới form Admin
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
    [InlineData("HR", false)]
    [InlineData("Admin", true)]
    public async Task Prompt_history_shows_activate_only_to_admin(string role, bool visible)
    {
        using var sp = ComponentServices(role);
        var html = await Html<PromptHistory>(sp, new() { ["Versions"] = Versions, ["SelectedId"] = 2 });
        Assert.Contains("v1", html);
        Assert.Equal(visible, html.Contains("Kích hoạt v1"));
    }

    [Theory]
    [InlineData("HR", false)]
    [InlineData("Admin", true)]
    public async Task Prompt_editor_is_read_only_for_hr_and_keeps_try_with_application(string role, bool canSave)
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
    public async Task Evaluation_set_creation_is_admin_only_but_hr_is_told_what_to_do(string role)
    {
        var api = Api();
        using var sp = ComponentServices(role, api);
        var html = await Html<EvaluationSetManager>(sp);
        Assert.Equal(role == "Admin", html.Contains("Tên bộ CV mới"));
        Assert.Equal(role == "Admin", html.Contains("id=\"create-set-open\""));
        Assert.Equal(role == "Admin", html.Contains("id=\"create-set-submit\""));
        Assert.Equal(role == "HR", html.Contains("Quản trị viên (Admin) tạo bộ"));
        Assert.All(BusinessCalls(api), c => Assert.StartsWith("GET ", c));
    }

    // ---------- Menu ----------

    [Fact]
    public async Task Hr_menu_has_every_reachable_page_and_a_mobile_account_block_with_role_and_logout()
    {
        var (raw, _, _) = await Open("HR", "forbidden");   // trang không cần API nào: chỉ kiểm tra khung + menu
        var html = System.Text.RegularExpressions.Regex.Replace(raw, @" b-[a-z0-9]{10}", "");
        foreach (var item in new[] { "Bảng điều khiển", "Tải hồ sơ lên", "Ứng viên", "Kết quả", "System prompt", "Bộ dữ liệu đánh giá", "Vị trí tuyển dụng", "Lịch sử lượt chấm" })
            Assert.Contains($"<span>{item}</span>", html);
        Assert.Contains("nav-account", html);
        Assert.Contains("nav-account-role", html);
        Assert.Contains("aria-label=\"Mở menu\"", html);
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(html, "class=\"nav-group\"").Count);
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
