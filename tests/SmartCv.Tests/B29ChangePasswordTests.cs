extern alias web;

using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Tests.Auth;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B29: đổi mật khẩu. (1) Luật form = PasswordRules của Core, không trim, không gửi trùng, không tự gửi lại; (2) mọi mã lỗi
/// A30 được phân biệt; (3) kết quả không rõ chỉ được kiểm tra bằng /api/auth/me; (4) token mới thay token cũ mà request cũ
/// trả 401 về sau không xóa phiên mới; (5) chạy thật với API A28/A30 trong TestServer (InMemory, tài khoản tổng hợp).
/// </summary>
public class B29ChangePasswordTests
{
    private const string Email = "lan.hr@example.invalid";

    private static ChangePasswordForm Form(string current = "TamThoi9x", string next = "MatKhauMoi2026", string? confirm = null) =>
        new(TimeProvider.System) { Current = current, New = next, Confirm = confirm ?? next };

    private static ChangePasswordResponse Response(string token = "new-token", bool mustChange = false) =>
        new() { Token = token, ExpiresAt = DateTime.UtcNow.AddHours(8), User = B27.User("HR", mustChange) };

    private static ApiResult<ChangePasswordResponse> Problem(int status, string code, Dictionary<string, string[]>? errors = null, TimeSpan? retry = null) =>
        ApiResult<ChangePasswordResponse>.Failure("x", status, code, retry, errors);

    // ---------- Luật và kiểm tra phía Web ----------

    [Theory]
    [InlineData("", "MatKhauMoi2026", "MatKhauMoi2026", "current")]
    [InlineData("TamThoi9x", "", "", "new")]
    [InlineData("TamThoi9x", "ngan1", "ngan1", "new")]                         // < 8 ký tự
    [InlineData("TamThoi9x", "chiCoChuThoi", "chiCoChuThoi", "new")]           // không có số
    [InlineData("TamThoi9x", " Khoangtrang9", " Khoangtrang9", "new")]         // khoảng trắng đầu
    [InlineData("TamThoi9x", "lan.hr@example.invalid", "lan.hr@example.invalid", "new")]   // trùng email (không có số → vẫn lỗi)
    [InlineData("TamThoi9x", "TamThoi9x", "TamThoi9x", "new")]                 // trùng mật khẩu hiện tại
    [InlineData("TamThoi9x", "MatKhauMoi2026", "MatKhauMoi2027", "confirm")]   // nhập lại không khớp
    [InlineData("TamThoi9x", "MatKhauMoi2026", "", "confirm")]
    public async Task Local_validation_blocks_the_request_and_points_at_the_field(string current, string next, string confirm, string field)
    {
        var api = new B27FakeAuthApi();
        var form = Form(current, next, confirm);
        Assert.Equal(ChangePasswordOutcome.NotSent, await form.SubmitAsync(api, "tok", Email));
        Assert.Empty(api.ChangeCalls);
        Assert.Equal(field == "current", form.CurrentError is not null);
        Assert.Equal(field == "new", form.NewError is not null);
        Assert.Equal(field == "confirm", form.ConfirmError is not null);
    }

    [Fact]
    public void Rule_checklist_follows_core_rules_while_typing()
    {
        var form = Form("TamThoi9x", "abc");
        Assert.Equal([false, false, true, true, true], form.Rules(Email).Select(r => r.Met));
        form.New = form.Confirm = "MatKhau 2026 moi";   // khoảng trắng ở giữa là hợp lệ
        Assert.All(form.Rules(Email), r => Assert.True(r.Met));
        Assert.True(form.Validate(Email));
    }

    [Fact]
    public async Task Success_sends_exact_untrimmed_values_once_and_forgets_every_password()
    {
        var api = new B27FakeAuthApi { ChangePassword = (_, _, _) => Task.FromResult(ApiResult<ChangePasswordResponse>.Success(Response())) };
        var form = Form("  TamThoi9x  ", "Mat khau moi 2026");
        Assert.Equal(ChangePasswordOutcome.Changed, await form.SubmitAsync(api, "old-token", Email));
        Assert.Equal(("old-token", "  TamThoi9x  ", "Mat khau moi 2026"), Assert.Single(api.ChangeCalls));
        Assert.Equal("new-token", form.Result!.Token);
        Assert.Equal(("", "", ""), (form.Current, form.New, form.Confirm));
        Assert.False(form.CanSubmit);   // đã xong: không gửi lần hai
    }

    [Fact]
    public async Task Double_click_while_pending_sends_one_request()
    {
        var gate = new TaskCompletionSource<ApiResult<ChangePasswordResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new B27FakeAuthApi { ChangePassword = (_, _, _) => gate.Task };
        var form = Form();
        var first = form.SubmitAsync(api, "tok", Email);
        Assert.True(form.Submitting);
        Assert.Equal(ChangePasswordOutcome.NotSent, await form.SubmitAsync(api, "tok", Email));
        gate.SetResult(ApiResult<ChangePasswordResponse>.Success(Response()));
        Assert.Equal(ChangePasswordOutcome.Changed, await first);
        Assert.Single(api.ChangeCalls);
    }

    [Fact]
    public async Task Wrong_current_password_is_a_field_error_and_is_cleared()
    {
        var api = new B27FakeAuthApi { ChangePassword = (_, _, _) => Task.FromResult(Problem(400, "validation_failed", new() { ["currentPassword"] = ["Mật khẩu hiện tại không đúng."] })) };
        var form = Form();
        Assert.Equal(ChangePasswordOutcome.Invalid, await form.SubmitAsync(api, "tok", Email));
        Assert.Equal("Mật khẩu hiện tại không đúng.", form.CurrentError);
        Assert.Equal("", form.Current);
        Assert.Equal(PasswordField.Current, form.FocusTarget);
        Assert.True(form.CanSubmit);   // chắc chắn chưa đổi → được thử lại
    }

    [Fact]
    public async Task Server_rejection_of_the_new_password_shows_under_the_new_field()
    {
        var api = new B27FakeAuthApi { ChangePassword = (_, _, _) => Task.FromResult(Problem(400, "validation_failed", new() { ["newPassword"] = ["Mật khẩu mới phải khác mật khẩu hiện tại."] })) };
        var form = Form();
        Assert.Equal(ChangePasswordOutcome.Invalid, await form.SubmitAsync(api, "tok", Email));
        Assert.Equal("Mật khẩu mới phải khác mật khẩu hiện tại.", form.NewError);
    }

    [Theory]
    [InlineData(401, "authentication_required", ChangePasswordOutcome.SessionEnded)]
    [InlineData(403, "access_denied", ChangePasswordOutcome.Failed)]
    [InlineData(409, "account_conflict", ChangePasswordOutcome.Failed)]
    [InlineData(429, "credential_rate_limited", ChangePasswordOutcome.RateLimited)]
    [InlineData(503, "account_unavailable", ChangePasswordOutcome.Unknown)]
    [InlineData(500, "account_error", ChangePasswordOutcome.Unknown)]
    public async Task Every_A30_failure_is_distinguished(int status, string code, ChangePasswordOutcome expected)
    {
        var api = new B27FakeAuthApi { ChangePassword = (_, _, _) => Task.FromResult(Problem(status, code, retry: TimeSpan.FromSeconds(42))) };
        var form = Form();
        Assert.Equal(expected, await form.SubmitAsync(api, "tok", Email));
        Assert.NotNull(form.FormError);
        Assert.DoesNotContain("x", form.FormError!.Split(' '));   // không dùng chữ thô của máy chủ
        if (status == 429) { Assert.True(form.IsRateLimited); Assert.InRange(form.RetrySecondsLeft, 41, 42); }
        Assert.Single(api.ChangeCalls);   // không bao giờ tự gửi lại
        // Chỉ cho người dùng thử lại khi CHẮC CHẮN chưa ghi (409); chưa biết kết quả / bị giới hạn / phiên hết → không.
        Assert.Equal(status == 409 || status == 403, form.CanSubmit);
    }

    [Theory]
    [InlineData(ApiFailureKind.Network)]
    [InlineData(ApiFailureKind.Timeout)]
    [InlineData(ApiFailureKind.InvalidData)]
    public async Task Unknown_outcome_locks_the_form_and_is_resolved_only_by_reading_auth_me(ApiFailureKind kind)
    {
        var me = ApiResult<MeDto>.Success(B27.User("HR", mustChange: true));
        var api = new B27FakeAuthApi
        {
            ChangePassword = (_, _, _) => Task.FromResult(ApiResult<ChangePasswordResponse>.LocalFailure("offline", kind)),
            Me = _ => me
        };
        var form = Form();
        Assert.Equal(ChangePasswordOutcome.Unknown, await form.SubmitAsync(api, "old", Email));
        Assert.True(form.Unresolved);
        Assert.False(form.CanSubmit);
        Assert.Equal(("", "", ""), (form.Current, form.New, form.Confirm));

        Assert.Equal(UnknownResolution.NotChanged, await form.ResolveUnknownAsync(api, "old"));   // token cũ còn dùng được
        Assert.True(form.CanSubmit);
        Assert.Single(api.ChangeCalls);                                                        // không gửi lại
        Assert.Equal(["old"], api.MeCalls);
    }

    [Theory]
    [InlineData(401, UnknownResolution.ProbablyChanged)]
    [InlineData(503, UnknownResolution.StillUnknown)]
    public async Task Unknown_outcome_with_a_revoked_or_unreachable_token(int meStatus, UnknownResolution expected)
    {
        var api = new B27FakeAuthApi
        {
            ChangePassword = (_, _, _) => Task.FromResult(Problem(503, "account_unavailable")),
            Me = _ => ApiResult<MeDto>.Failure("x", meStatus)
        };
        var form = Form();
        await form.SubmitAsync(api, "old", Email);
        Assert.Equal(expected, await form.ResolveUnknownAsync(api, "old"));
        Assert.False(form.CanSubmit);   // chưa biết hoặc đã đổi: không cho gửi lại
    }

    [Fact]
    public async Task Success_without_a_token_is_treated_as_unknown_not_as_success()
    {
        var api = new B27FakeAuthApi { ChangePassword = (_, _, _) => Task.FromResult(ApiResult<ChangePasswordResponse>.Success(new ChangePasswordResponse())) };
        var form = Form();
        Assert.Equal(ChangePasswordOutcome.Unknown, await form.SubmitAsync(api, "tok", Email));
        Assert.Null(form.Result);
    }

    [Theory]
    [InlineData(null, "change-password", "/")]
    [InlineData("/jobs?x=1", "change-password?returnUrl=%2Fjobs%3Fx%3D1", "/jobs?x=1")]
    [InlineData("/change-password", "change-password", "/")]
    [InlineData("/login", "change-password", "/")]
    [InlineData("https://evil.example", "change-password", "/")]
    [InlineData("//evil.example", "change-password", "/")]
    public void Return_urls_stay_inside_the_app_and_never_loop(string? current, string url, string after)
    {
        Assert.Equal(url, AuthText.ChangePasswordUrl(current));
        Assert.Equal(after, AuthText.AfterPasswordChange(current));
    }

    // ---------- Sửa lỗi B28 tìm thấy khi kiểm thử trình duyệt B29 ----------

    [Fact]
    public async Task Deep_link_while_signed_out_then_login_in_the_same_circuit_keeps_logout_working()
    {
        // Mở /candidates khi chưa đăng nhập → /login TRONG circuit → đăng nhập → Đăng xuất / Đổi mật khẩu phải còn dùng được.
        var session = B27.Session();
        await session.EnsureRestoredAsync();
        var nav = new B28AuthStateProviderTests.B28Nav("https://app.test/candidates");
        var navigator = new AuthNavigator(session, nav);
        navigator.ToLogin();
        navigator.ToLogin();   // AuthGate + RedirectToLogin: vẫn chỉ một lần
        Assert.Equal(("login?returnUrl=%2Fcandidates", false, true), Assert.Single(nav.Navigations));
        Assert.False(navigator.IsLeaving);

        await session.SignInAsync(B27.Login("tok", "HR"));
        await navigator.SignOutAsync();
        Assert.Equal(("login?signedOut=1", true, true), nav.Navigations.Last());
        Assert.False(session.IsSignedIn);
        Assert.True(navigator.IsLeaving);
    }

    // ---------- Token mới thay token cũ ----------

    [Fact]
    public async Task A_late_401_for_the_old_token_does_not_end_the_renewed_session()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new B27CaptureHandler
        {
            Respond = async request =>
            {
                if (request.Headers.Authorization?.Parameter == "old-token") { await gate.Task; return B27CaptureHandler.Problem(HttpStatusCode.Unauthorized, "authentication_required"); }
                return B27CaptureHandler.Json(HttpStatusCode.OK, Array.Empty<JobDto>());
            }
        };
        await using var web = B27.WebServices(api);
        await using var tab = web.CreateAsyncScope();
        var session = tab.ServiceProvider.GetRequiredService<AuthSession>();
        await session.SignInAsync(B27.Login("old-token", "HR", mustChange: true));
        var expired = 0; session.Expired += () => expired++;

        var inFlight = tab.ServiceProvider.GetRequiredService<JobApiClient>().GetJobsAsync();   // gửi bằng token cũ
        var r = Response("new-token");
        await session.SignInAsync(new LoginResponse { Token = r.Token, ExpiresAt = r.ExpiresAt, User = r.User });
        gate.SetResult();
        Assert.Equal(401, (await inFlight).StatusCode);

        Assert.True(session.IsSignedIn);
        Assert.False(session.MustChangePassword);
        Assert.Equal(0, expired);
        Assert.True((await tab.ServiceProvider.GetRequiredService<JobApiClient>().GetJobsAsync()).IsSuccess);
        Assert.Equal("Bearer new-token", api.Calls.Last().Authorization);
        var state = await tab.ServiceProvider.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
        Assert.Equal(AccessLevel.Hr, AccessRules.Level(state.User));
    }

    // ---------- API A28/A30 thật ----------

    [Fact]
    public async Task Temporary_password_account_changes_password_through_the_real_api_and_opens_the_app()
    {
        using var api = A28TestSupport.Factory();
        var temporary = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, temporary, "HR", mustChange: true);
        await using var webHost = B27.WebServices(api.Server.CreateHandler());
        await using var tab = webHost.CreateAsyncScope();
        var sp = tab.ServiceProvider;
        var login = await new LoginForm(TimeProvider.System) { Email = user.Email, Password = temporary }.SubmitAsync(sp.GetRequiredService<IAuthApi>());
        var session = sp.GetRequiredService<AuthSession>();
        await session.SignInAsync(login!);
        Assert.Equal(AccessLevel.PasswordChangeRequired, AccessRules.Level((await sp.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User));
        var oldToken = login!.Token;

        // Sai mật khẩu hiện tại → 400, chưa đổi.
        var wrong = new ChangePasswordForm(TimeProvider.System) { Current = "SaiMatKhau9", New = "MatKhauRieng2026", Confirm = "MatKhauRieng2026" };
        Assert.Equal(ChangePasswordOutcome.Invalid, await wrong.SubmitAsync(sp.GetRequiredService<IAuthApi>(), oldToken, user.Email));
        Assert.NotNull(wrong.CurrentError);

        var form = new ChangePasswordForm(TimeProvider.System) { Current = temporary, New = "MatKhauRieng2026", Confirm = "MatKhauRieng2026" };
        Assert.Equal(ChangePasswordOutcome.Changed, await form.SubmitAsync(sp.GetRequiredService<IAuthApi>(), oldToken, user.Email));
        var r = form.Result!;
        Assert.NotEqual(oldToken, r.Token);
        Assert.False(r.User.MustChangePassword);
        await session.SignInAsync(new LoginResponse { Token = r.Token, ExpiresAt = r.ExpiresAt, User = r.User });

        Assert.Equal(AccessLevel.Hr, AccessRules.Level((await sp.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User));
        Assert.True((await sp.GetRequiredService<JobApiClient>().GetJobsAsync()).IsSuccess);                       // nghiệp vụ mở bằng token mới
        Assert.Equal(401, (await sp.GetRequiredService<IAuthApi>().GetMeAsync(oldToken)).StatusCode);              // token cũ hết hiệu lực
        Assert.Equal(401, (await sp.GetRequiredService<IAuthApi>().LoginAsync(user.Email, temporary)).StatusCode); // mật khẩu tạm hết tác dụng
        Assert.True((await sp.GetRequiredService<IAuthApi>().LoginAsync(user.Email, "MatKhauRieng2026")).IsSuccess);
    }

    [Fact]
    public async Task Business_apis_stay_closed_until_the_password_is_changed()
    {
        using var api = A28TestSupport.Factory();
        var temporary = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(api, temporary, "HR", mustChange: true);
        await using var webHost = B27.WebServices(api.Server.CreateHandler());
        await using var tab = webHost.CreateAsyncScope();
        var sp = tab.ServiceProvider;
        await sp.GetRequiredService<AuthSession>().SignInAsync((await new LoginForm(TimeProvider.System) { Email = user.Email, Password = temporary }.SubmitAsync(sp.GetRequiredService<IAuthApi>()))!);
        var jobs = await sp.GetRequiredService<JobApiClient>().GetJobsAsync();
        Assert.Equal(403, jobs.StatusCode);
        Assert.Equal("password_change_required", jobs.ErrorCode);
        Assert.True(sp.GetRequiredService<AuthSession>().IsSignedIn);   // 403 không đăng xuất
    }
}
