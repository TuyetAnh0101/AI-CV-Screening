extern alias web;

using SmartCv.Core.Contracts;
using SmartCv.Tests.Auth;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>B27: luật thuần của đăng nhập phía Web — địa chỉ quay lại, câu lỗi, form, phiên của một tab.</summary>
public class B27AuthSessionTests
{
    // ---------- AuthText ----------

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/cvs?search=java", "/cvs?search=java")]
    [InlineData("cvs", "/cvs")]
    [InlineData("/runs/15", "/runs/15")]
    [InlineData("https://lua-dao.vn", "/")]
    [InlineData("http://localhost:7258/cvs", "/")]
    [InlineData("//lua-dao.vn", "/")]
    [InlineData("/\\lua-dao.vn", "/")]
    [InlineData("\\\\lua-dao.vn", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/login", "/")]
    [InlineData("/LOGIN/?expired=1", "/")]
    [InlineData("/cvs\r\nSet-Cookie: x=1", "/")]
    public void SafeReturnUrl_only_accepts_paths_inside_the_app(string? input, string expected) =>
        Assert.Equal(expected, AuthText.SafeReturnUrl(input));

    [Fact]
    public void LoginUrl_keeps_the_current_page_and_marks_expiry()
    {
        Assert.Equal("login?returnUrl=%2Fcvs%3Fsearch%3Djava", AuthText.LoginUrl("/cvs?search=java"));
        Assert.Equal("login?returnUrl=%2Fruns&expired=1", AuthText.LoginUrl("/runs", expired: true));
        Assert.Equal("login", AuthText.LoginUrl("/"));
        Assert.Equal("login?expired=1", AuthText.LoginUrl("https://lua-dao.vn", expired: true));
    }

    [Theory]
    [InlineData("Nguyễn Thị Lan", "NL")]
    [InlineData("  lan  ", "L")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Initials_are_first_and_last_word(string? name, string expected) => Assert.Equal(expected, AuthText.Initials(name));

    [Fact]
    public void Role_names_are_Vietnamese() =>
        Assert.Equal(["Quản trị viên", "Nhân sự (HR)", "Người dùng"], new[] { "Admin", "HR", "X" }.Select(AuthText.RoleName));

    // ---------- LoginForm ----------

    private static (LoginForm Form, B27FakeAuthApi Api, A28TestSupport.Clock Clock) NewForm()
    {
        var clock = new A28TestSupport.Clock(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero));
        return (new LoginForm(clock), new B27FakeAuthApi(), clock);
    }

    private static Func<string, string, Task<ApiResult<LoginResponse>>> Fails(int status, string? code = null, TimeSpan? retry = null) =>
        (_, _) => Task.FromResult(ApiResult<LoginResponse>.Failure("RAW SERVER TEXT", status, code, retry));

    [Theory]
    [InlineData("", "abc12345", "Vui lòng nhập email.", null)]
    [InlineData("   ", "abc12345", "Vui lòng nhập email.", null)]
    [InlineData("khong-phai-email", "abc12345", "Email chưa đúng định dạng (ví dụ: ten@congty.vn).", null)]
    [InlineData("Tên <a@b.vn>", "abc12345", "Email chưa đúng định dạng (ví dụ: ten@congty.vn).", null)]
    [InlineData("a@b.vn", "", null, "Vui lòng nhập mật khẩu.")]
    public async Task Invalid_input_is_caught_before_calling_the_API(string email, string password, string? emailError, string? passwordError)
    {
        var (form, api, _) = NewForm();
        form.Email = email; form.Password = password;
        Assert.Null(await form.SubmitAsync(api));
        Assert.Empty(api.LoginCalls);
        Assert.Equal(emailError, form.EmailError);
        Assert.Equal(passwordError, form.PasswordError);
        Assert.Equal(emailError is not null ? LoginField.Email : LoginField.Password, form.FocusTarget);
    }

    [Fact]
    public async Task Overlong_password_is_rejected_locally()
    {
        var (form, api, _) = NewForm();
        form.Email = "a@b.vn"; form.Password = new string('x', 129);
        Assert.Null(await form.SubmitAsync(api));
        Assert.Empty(api.LoginCalls);
        Assert.Equal("Mật khẩu tối đa 128 ký tự.", form.PasswordError);
    }

    [Fact]
    public async Task Success_sends_normalized_email_once_and_forgets_the_password()
    {
        var (form, api, _) = NewForm();
        api.Login = (_, _) => Task.FromResult(ApiResult<LoginResponse>.Success(B27.Login("tok")));
        form.Email = "  Lan.HR@Example.VN "; form.Password = "Secret-123";
        var result = await form.SubmitAsync(api);
        Assert.Equal("tok", result!.Token);
        Assert.Equal(("lan.hr@example.vn", "Secret-123"), Assert.Single(api.LoginCalls));
        Assert.Equal("", form.Password);
        Assert.Null(form.FormError);
    }

    [Fact]
    public async Task Second_submit_while_the_first_is_in_flight_does_not_call_the_API_again()
    {
        var (form, api, _) = NewForm();
        var gate = new TaskCompletionSource<ApiResult<LoginResponse>>();
        api.Login = (_, _) => gate.Task;
        form.Email = "a@b.vn"; form.Password = "Secret-123";
        var first = form.SubmitAsync(api);
        Assert.True(form.Submitting); Assert.False(form.CanSubmit);
        Assert.Null(await form.SubmitAsync(api));      // Enter lần hai / bấm nút lần hai
        gate.SetResult(ApiResult<LoginResponse>.Success(B27.Login("tok")));
        Assert.NotNull(await first);
        Assert.Single(api.LoginCalls);
        Assert.False(form.Submitting);
    }

    [Theory]
    [InlineData(401, "invalid_credentials", "Email hoặc mật khẩu không đúng.")]
    [InlineData(401, null, "Email hoặc mật khẩu không đúng.")]
    [InlineData(403, "account_locked", "Tài khoản này đã bị khóa. Liên hệ quản trị viên (Admin) nếu cần mở lại.")]
    [InlineData(403, "access_denied", "Tài khoản này hiện không được phép đăng nhập. Liên hệ quản trị viên (Admin).")]
    public async Task Credential_failures_use_fixed_text_clear_the_password_and_focus_it(int status, string? code, string expected)
    {
        var (form, api, _) = NewForm();
        api.Login = Fails(status, code);
        form.Email = "a@b.vn"; form.Password = "Secret-123";
        Assert.Null(await form.SubmitAsync(api));
        Assert.Equal(expected, form.FormError);
        Assert.DoesNotContain("RAW SERVER TEXT", form.FormError);
        Assert.Equal("", form.Password);
        Assert.Equal(LoginField.Password, form.FocusTarget);
    }

    [Fact]
    public async Task Rate_limit_honours_Retry_After_then_allows_a_new_attempt()
    {
        var (form, api, clock) = NewForm();
        api.Login = Fails(429, "login_rate_limited", TimeSpan.FromSeconds(30));
        form.Email = "a@b.vn"; form.Password = "Secret-123";
        await form.SubmitAsync(api);
        Assert.True(form.IsRateLimited); Assert.False(form.CanSubmit);
        Assert.Equal(30, form.RetrySecondsLeft);
        Assert.Equal("Bạn đã thử đăng nhập quá nhiều lần. Vui lòng đợi rồi thử lại.", form.FormError);

        form.Password = "Secret-123";
        Assert.Null(await form.SubmitAsync(api));       // còn bị chặn: không gửi
        Assert.Single(api.LoginCalls);

        clock.Advance(TimeSpan.FromSeconds(31));
        form.ClearExpiredRateLimit();
        Assert.True(form.CanSubmit); Assert.Null(form.FormError);
    }

    [Fact]
    public async Task Rate_limit_without_Retry_After_waits_one_minute()
    {
        var (form, api, _) = NewForm();
        api.Login = Fails(429);
        form.Email = "a@b.vn"; form.Password = "Secret-123";
        await form.SubmitAsync(api);
        Assert.Equal(60, form.RetrySecondsLeft);
    }

    [Fact]
    public async Task Service_and_network_failures_are_not_reported_as_wrong_password_and_keep_the_password()
    {
        var cases = new (Func<string, string, Task<ApiResult<LoginResponse>>> Login, string Expected)[]
        {
            (Fails(503, "auth_unavailable"), "Dịch vụ đăng nhập tạm thời không khả dụng. Vui lòng thử lại sau ít phút."),
            (Fails(500), "Máy chủ gặp sự cố khi đăng nhập. Vui lòng thử lại sau."),
            ((_, _) => Task.FromResult(ApiResult<LoginResponse>.LocalFailure("x", ApiFailureKind.Network)), "Không kết nối được máy chủ SCApp. Kiểm tra kết nối mạng hoặc thử lại sau ít phút."),
            ((_, _) => Task.FromResult(ApiResult<LoginResponse>.LocalFailure("x", ApiFailureKind.Timeout)), "Máy chủ phản hồi quá lâu. Vui lòng thử lại."),
        };
        foreach (var (login, expected) in cases)
        {
            var (form, api, _) = NewForm();
            api.Login = login;
            form.Email = "a@b.vn"; form.Password = "Secret-123";
            await form.SubmitAsync(api);
            Assert.Equal(expected, form.FormError);
            Assert.NotEqual(AuthText.InvalidCredentials, form.FormError);
            Assert.Equal("Secret-123", form.Password);
            Assert.Equal(LoginField.Submit, form.FocusTarget);
        }
    }

    [Fact]
    public async Task Server_validation_400_marks_the_fields_without_echoing_server_text()
    {
        var (form, api, _) = NewForm();
        api.Login = (_, _) => Task.FromResult(ApiResult<LoginResponse>.Failure("RAW", 400, "invalid_request", null,
            new Dictionary<string, string[]> { ["email"] = ["RAW EMAIL"] }));
        form.Email = "a@b.vn"; form.Password = "Secret-123";
        await form.SubmitAsync(api);
        Assert.Equal("Email chưa đúng định dạng.", form.EmailError);
        Assert.Equal(LoginField.Email, form.FocusTarget);
        Assert.DoesNotContain("RAW", form.FormError);
    }

    [Fact]
    public async Task Success_without_a_token_is_not_treated_as_signed_in()
    {
        var (form, api, _) = NewForm();
        api.Login = (_, _) => Task.FromResult(ApiResult<LoginResponse>.Success(new LoginResponse { Token = "", User = B27.User() }));
        form.Email = "a@b.vn"; form.Password = "Secret-123";
        Assert.Null(await form.SubmitAsync(api));
        Assert.Equal("Máy chủ trả về dữ liệu không hợp lệ. Vui lòng thử lại sau.", form.FormError);
    }

    // ---------- AuthSession ----------

    private static AuthSnapshot Saved(string token, string role = "HR", TimeSpan? left = null) =>
        new(token, DateTime.UtcNow + (left ?? TimeSpan.FromHours(4)), B27.User(role));

    [Fact]
    public async Task Refresh_restores_a_saved_session_only_after_api_auth_me_confirms_it()
    {
        var store = new B27MemoryStore { Saved = Saved("tok-saved", role: "HR") };
        var api = new B27FakeAuthApi { Me = _ => ApiResult<MeDto>.Success(B27.User("Admin", name: "Tên mới")) };
        var session = B27.Session(store, api);
        await session.EnsureRestoredAsync();
        Assert.True(session.IsSignedIn); Assert.True(session.IsVerified);
        Assert.Equal("tok-saved", Assert.Single(api.MeCalls));
        Assert.Equal("Admin", session.User!.Role);                     // vai trò mới nhất từ database
        Assert.Equal("Tên mới", store.Saved!.User.FullName);
    }

    [Fact]
    public async Task Revoked_saved_token_is_cleared_and_reported_as_expired()
    {
        var store = new B27MemoryStore { Saved = Saved("tok-old") };
        var api = new B27FakeAuthApi { Me = _ => ApiResult<MeDto>.Failure("x", 401, "authentication_required") };
        var session = B27.Session(store, api);
        await session.EnsureRestoredAsync();
        Assert.False(session.IsSignedIn);
        Assert.Equal(AuthEndReason.Expired, session.EndReason);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task Locally_expired_token_is_dropped_without_calling_the_API()
    {
        var store = new B27MemoryStore { Saved = Saved("tok-old", left: TimeSpan.FromMinutes(-1)) };
        var api = new B27FakeAuthApi();
        var session = B27.Session(store, api);
        await session.EnsureRestoredAsync();
        Assert.False(session.IsSignedIn);
        Assert.Empty(api.MeCalls);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task API_outage_during_refresh_keeps_the_session_unverified_instead_of_signing_out()
    {
        var store = new B27MemoryStore { Saved = Saved("tok") };
        var api = new B27FakeAuthApi { Me = _ => ApiResult<MeDto>.LocalFailure("x", ApiFailureKind.Network) };
        var session = B27.Session(store, api);
        await session.EnsureRestoredAsync();
        Assert.True(session.IsSignedIn);
        Assert.False(session.IsVerified);
        Assert.NotNull(store.Saved);
    }

    [Fact]
    public async Task Storage_failure_is_signed_out_and_never_throws()
    {
        var store = new B27MemoryStore { FailLoad = new InvalidOperationException("prerender") };
        var session = B27.Session(store);
        await session.EnsureRestoredAsync();
        Assert.False(session.IsSignedIn);
        Assert.True(session.IsRestored);
    }

    [Fact]
    public async Task Restore_runs_once_for_the_whole_tab()
    {
        var store = new B27MemoryStore { Saved = Saved("tok") };
        var api = new B27FakeAuthApi { Me = _ => ApiResult<MeDto>.Success(B27.User()) };
        var session = B27.Session(store, api);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => session.EnsureRestoredAsync()));
        Assert.Equal(1, store.Loads);
        Assert.Single(api.MeCalls);
    }

    [Fact]
    public async Task Sign_in_saves_token_and_user_and_sign_out_clears_memory_and_storage()
    {
        var store = new B27MemoryStore();
        var session = B27.Session(store);
        var changed = 0; session.Changed += () => changed++;
        await session.SignInAsync(B27.Login("tok", "HR"));
        Assert.True(session.IsSignedIn); Assert.False(session.IsAdmin);
        Assert.Equal("tok", store.Saved!.Token);
        await session.SignOutAsync();
        Assert.False(session.IsSignedIn); Assert.Null(session.User);
        Assert.Null(store.Saved);
        Assert.Equal(AuthEndReason.SignedOut, session.EndReason);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Snapshot_type_has_no_password_field() =>
        Assert.DoesNotContain(typeof(AuthSnapshot).GetProperties().Concat(typeof(MeDto).GetProperties()),
            p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) && p.Name != nameof(MeDto.MustChangePassword));
}
