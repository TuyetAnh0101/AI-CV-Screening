extern alias web;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B28: ApiAuthenticationStateProvider chỉ là lớp đọc trên AuthSession (B27). Vai trò lấy từ /api/auth/me hoặc phản hồi
/// đăng nhập, không bao giờ từ storage; mọi thay đổi của phiên được báo cho AuthorizeRouteView; fail-closed khi chưa xác minh.
/// </summary>
public class B28AuthStateProviderTests
{
    internal sealed class B28Nav : NavigationManager
    {
        public List<(string Uri, bool ForceLoad, bool Replace)> Navigations { get; } = [];
        public B28Nav(string uri = "https://app.test/") => Initialize("https://app.test/", uri);
        protected override void NavigateToCore(string uri, bool forceLoad) => NavigateToCore(uri, new NavigationOptions { ForceLoad = forceLoad });
        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Navigations.Add((uri, options.ForceLoad, options.ReplaceHistoryEntry));
            Uri = ToAbsoluteUri(uri).AbsoluteUri;
        }
    }

    /// <summary>"sessionStorage" mà lần đọc đầu chờ cho tới khi test cho phép (mô phỏng circuit vừa kết nối, JS chưa trả lời).</summary>
    private sealed class SlowStore(AuthSnapshot? saved) : ITokenStore
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AuthSnapshot?> LoadAsync() { await Release.Task; return saved; }
        public Task SaveAsync(AuthSnapshot snapshot) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static B27MemoryStore Saved(string role = "HR", string token = "saved-token") =>
        new() { Saved = new(token, DateTime.UtcNow.AddHours(4), B27.User(role)) };

    private static ApiResult<MeDto> Me(string role, bool mustChange = false) => ApiResult<MeDto>.Success(B27.User(role, mustChange));

    private static (ApiAuthenticationStateProvider Provider, List<AuthenticationState> Notified) Watch(AuthSession session)
    {
        var provider = new ApiAuthenticationStateProvider(session);
        var notified = new List<AuthenticationState>();
        provider.AuthenticationStateChanged += t => notified.Add(t.GetAwaiter().GetResult());
        return (provider, notified);
    }

    [Fact]
    public async Task Anonymous_tab_is_not_authenticated_and_calls_no_API()
    {
        var api = new B27FakeAuthApi();
        var (provider, _) = Watch(B27.Session(new B27MemoryStore(), api));
        var state = await provider.GetAuthenticationStateAsync();
        Assert.False(state.User.Identity!.IsAuthenticated);
        Assert.Equal(AccessLevel.Anonymous, AccessRules.Level(state.User));
        Assert.Empty(api.MeCalls);
    }

    [Fact]
    public async Task Refresh_takes_the_role_from_api_auth_me_not_from_the_stored_snapshot()
    {
        // Phiên đã lưu ghi HR; Admin vừa nâng quyền → /me trả Admin. Hỏi /me đúng một lần cho cả tab.
        var api = new B27FakeAuthApi { Me = _ => Me("Admin") };
        var (provider, _) = Watch(B27.Session(Saved("HR"), api));
        var state = await provider.GetAuthenticationStateAsync();
        await provider.GetAuthenticationStateAsync();
        Assert.Equal(AccessLevel.Admin, AccessRules.Level(state.User));
        Assert.True(state.User.IsInRole("Admin"));
        Assert.Equal("Nguyễn Thị Lan", state.User.Identity!.Name);
        Assert.Single(api.MeCalls);
    }

    [Fact]
    public async Task Claims_never_contain_the_token()
    {
        var api = new B27FakeAuthApi { Me = _ => Me("HR") };
        var (provider, _) = Watch(B27.Session(Saved("HR", token: "SECRET-JWT-VALUE"), api));
        var state = await provider.GetAuthenticationStateAsync();
        Assert.DoesNotContain(state.User.Claims, c => c.Value.Contains("SECRET-JWT-VALUE"));
        // A verified internal HR session also carries its workspace kind/role (A52), never the token.
        Assert.Equal(["sub", "name", "email", AccessRules.SessionClaim, AccessRules.WorkspaceKindClaim, AccessRules.WorkspaceRoleClaim, "role", "smartcv_mcp"], state.User.Claims.Select(c => c.Type));
    }

    [Fact]
    public async Task While_the_session_is_being_restored_the_state_is_pending_so_no_page_is_created()
    {
        var store = new SlowStore(new("t", DateTime.UtcNow.AddHours(1), B27.User("HR")));
        var session = new AuthSession(store, new B27FakeAuthApi { Me = _ => Me("HR") }, TimeProvider.System, NullLogger<AuthSession>.Instance);
        var (provider, _) = Watch(session);
        var pending = provider.GetAuthenticationStateAsync();
        Assert.False(pending.IsCompleted);                              // AuthorizeRouteView hiện "Authorizing"
        Assert.False(AccessRules.Can(pending, AppPermission.ManageJobs));
        store.Release.SetResult();
        Assert.Equal(AccessLevel.Hr, AccessRules.Level((await pending).User));
    }

    [Fact]
    public async Task Api_unreachable_on_refresh_keeps_the_session_but_grants_nothing()
    {
        var api = new B27FakeAuthApi { Me = _ => ApiResult<MeDto>.LocalFailure("offline", ApiFailureKind.Network) };
        var session = B27.Session(Saved("Admin"), api);
        var (provider, _) = Watch(session);
        var user = (await provider.GetAuthenticationStateAsync()).User;
        Assert.Equal(AccessLevel.Unverified, AccessRules.Level(user));   // không về /login, không phải 403
        Assert.False(user.IsInRole("Admin"));
        Assert.True(session.IsSignedIn);
        Assert.Equal(AuthEndReason.None, session.EndReason);
    }

    [Fact]
    public async Task Revoked_saved_session_becomes_anonymous_with_the_expired_reason()
    {
        var api = new B27FakeAuthApi { Me = _ => ApiResult<MeDto>.Failure("x", 401) };
        var session = B27.Session(Saved("Admin"), api);
        var (provider, _) = Watch(session);
        Assert.Equal(AccessLevel.Anonymous, AccessRules.Level((await provider.GetAuthenticationStateAsync()).User));
        Assert.Equal(AuthEndReason.Expired, session.EndReason);
        Assert.False(session.HadSessionInCircuit);   // chưa từng có dữ liệu trong circuit → không cần tải lại trang
    }

    [Fact]
    public async Task Sign_in_and_sign_out_notify_with_the_new_state()
    {
        var session = B27.Session();
        var (provider, notified) = Watch(session);
        await provider.GetAuthenticationStateAsync();
        await session.SignInAsync(B27.Login("t1", "HR"));
        await session.SignOutAsync();
        Assert.Equal([AccessLevel.Hr, AccessLevel.Anonymous], notified.Select(s => AccessRules.Level(s.User)));
        Assert.Equal(AccessLevel.Anonymous, AccessRules.Level((await provider.GetAuthenticationStateAsync()).User));
    }

    [Fact]
    public async Task Api_401_mid_session_notifies_anonymous_once_and_stops_calling()
    {
        await using var tab = await B28Tab.SignedInAsync("Admin");
        var notified = new List<AuthenticationState>();
        tab.Get<AuthenticationStateProvider>().AuthenticationStateChanged += t => notified.Add(t.GetAwaiter().GetResult());

        var revoked = await tab.RevokeAsync();
        var after = await tab.Get<JobApiClient>().GetJobsAsync();   // không gửi lại, không spam 401
        Assert.Equal(401, revoked.StatusCode);
        Assert.Equal(ApiFailureKind.SignedOut, after.FailureKind);
        Assert.Single(tab.Api.Calls);
        Assert.Equal(AccessLevel.Anonymous, AccessRules.Level(Assert.Single(notified).User));
        Assert.Equal(AuthEndReason.Expired, tab.Session.EndReason);
        Assert.True(tab.Session.HadSessionInCircuit);
    }

    [Fact]
    public async Task Password_change_required_403_keeps_the_session_and_blocks_business_pages()
    {
        await using var tab = await B28Tab.SignedInAsync("HR");
        var notified = new List<AuthenticationState>();
        tab.Get<AuthenticationStateProvider>().AuthenticationStateChanged += t => notified.Add(t.GetAwaiter().GetResult());
        var result = await tab.RequirePasswordChangeAsync();
        Assert.Equal(403, result.StatusCode);
        Assert.Equal(AccessLevel.PasswordChangeRequired, AccessRules.Level(Assert.Single(notified).User));
        Assert.True(tab.Session.IsSignedIn);
    }

    [Fact]
    public async Task Unknown_role_is_fail_closed()
    {
        var session = B27.Session();
        await session.SignInAsync(B27.Login("t1", "Manager"));
        var user = (await new ApiAuthenticationStateProvider(session).GetAuthenticationStateAsync()).User;
        Assert.Equal(AccessLevel.NoValidRole, AccessRules.Level(user));
        Assert.DoesNotContain(user.Claims, c => c.Type == "role");
    }

    [Fact]
    public async Task Token_past_its_expiry_is_anonymous_even_without_an_event()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        var session = B27.Session(time: time);
        await session.SignInAsync(new LoginResponse { Token = "t", ExpiresAt = time.Now.UtcDateTime.AddMinutes(5), User = B27.User("HR") });
        var provider = new ApiAuthenticationStateProvider(session);
        Assert.Equal(AccessLevel.Hr, AccessRules.Level((await provider.GetAuthenticationStateAsync()).User));
        time.Now = time.Now.AddMinutes(6);
        Assert.Equal(AccessLevel.Anonymous, AccessRules.Level((await provider.GetAuthenticationStateAsync()).User));
    }

    [Fact]
    public async Task Retry_after_network_failure_verifies_and_notifies_the_fresh_role()
    {
        var online = false;
        var api = new B27FakeAuthApi { Me = _ => online ? Me("Admin") : ApiResult<MeDto>.LocalFailure("offline", ApiFailureKind.Network) };
        var session = B27.Session(Saved("HR"), api);
        var (provider, notified) = Watch(session);
        await provider.GetAuthenticationStateAsync();

        Assert.False(await session.VerifyAsync());                    // vẫn mất kết nối: không đổi gì, không báo
        Assert.Empty(notified);
        online = true;
        var a = session.VerifyAsync();
        var b = session.VerifyAsync();                                // bấm trùng: dùng chung một lần gọi
        Assert.True(await a); Assert.True(await b);
        Assert.Equal(AccessLevel.Admin, AccessRules.Level(Assert.Single(notified).User));
        Assert.Equal(3, api.MeCalls.Count);                           // khôi phục + thử lỗi + một lần thử thành công
    }

    [Fact]
    public async Task Retry_that_finds_the_session_revoked_ends_it()
    {
        var revoked = false;
        var api = new B27FakeAuthApi { Me = _ => revoked ? ApiResult<MeDto>.Failure("x", 401) : ApiResult<MeDto>.LocalFailure("offline", ApiFailureKind.Network) };
        var session = B27.Session(Saved("HR"), api);
        var expired = 0; session.Expired += () => expired++;
        await session.EnsureRestoredAsync();
        revoked = true;
        Assert.False(await session.VerifyAsync());
        Assert.False(session.IsSignedIn);
        Assert.Equal(1, expired);
    }

    [Fact]
    public async Task Disposed_provider_stops_listening()
    {
        var session = B27.Session();
        var (provider, notified) = Watch(session);
        provider.Dispose();
        await session.SignInAsync(B27.Login("t", "Admin"));
        Assert.Empty(notified);
    }

    [Fact]
    public async Task Two_circuits_never_share_the_authentication_state()
    {
        await using var web = B27.WebServices(new B27CaptureHandler());
        await using var tabA = web.CreateAsyncScope();
        await using var tabB = web.CreateAsyncScope();
        var a = tabA.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        var b = tabB.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        Assert.IsType<ApiAuthenticationStateProvider>(a);
        Assert.NotSame(a, b);
        var bChanged = 0; b.AuthenticationStateChanged += _ => bChanged++;

        await tabA.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(B27.Login("token-A", "Admin"));

        Assert.Equal(AccessLevel.Admin, AccessRules.Level((await a.GetAuthenticationStateAsync()).User));
        Assert.Equal(AccessLevel.Anonymous, AccessRules.Level((await b.GetAuthenticationStateAsync()).User));
        Assert.Equal(0, bChanged);
    }

    // ---------- AuthNavigator: một lần chuyển trang ----------

    private static (AuthNavigator Navigator, B28Nav Nav) Navigator(AuthSession session, string uri)
    {
        var nav = new B28Nav(uri);
        return (new AuthNavigator(session, nav), nav);
    }

    [Fact]
    public async Task First_visit_goes_to_login_in_the_same_circuit_once_and_keeps_a_safe_return_url()
    {
        var session = B27.Session();
        await session.EnsureRestoredAsync();
        var (navigator, nav) = Navigator(session, "https://app.test/jobs/new?x=1");
        navigator.ToLogin();
        navigator.ToLogin();   // AuthGate và RedirectToLogin cùng phản ứng
        Assert.Equal(("login?returnUrl=%2Fjobs%2Fnew%3Fx%3D1", false, true), Assert.Single(nav.Navigations));
    }

    [Fact]
    public async Task Return_url_never_leaves_the_app()
    {
        var session = B27.Session();
        await session.EnsureRestoredAsync();
        var (navigator, nav) = Navigator(session, "https://app.test//evil.example/path");
        navigator.ToLogin();
        Assert.Equal("login", Assert.Single(nav.Navigations).Uri);
    }

    [Fact]
    public async Task Session_ended_mid_circuit_reloads_login_with_the_expired_notice()
    {
        await using var tab = await B28Tab.SignedInAsync("HR");
        await tab.RevokeAsync();
        var (navigator, nav) = Navigator(tab.Session, "https://app.test/runs/5");
        navigator.ToLogin();
        navigator.ToLogin();
        Assert.Equal(("login?returnUrl=%2Fruns%2F5&expired=1", true, true), Assert.Single(nav.Navigations));
    }

    [Fact]
    public async Task Sign_out_clears_the_session_and_reloads_login_exactly_once()
    {
        var store = new B27MemoryStore();
        var session = B27.Session(store);
        await session.SignInAsync(B27.Login("t", "Admin"));
        var (navigator, nav) = Navigator(session, "https://app.test/prompts");
        session.Changed += navigator.ToLogin;        // như RedirectToLogin vẽ lại khi trạng thái đổi
        await navigator.SignOutAsync();
        await navigator.SignOutAsync();
        Assert.Equal(("login?signedOut=1", true, true), Assert.Single(nav.Navigations));
        Assert.Null(store.Saved);
        Assert.False(session.IsSignedIn);
    }
}
