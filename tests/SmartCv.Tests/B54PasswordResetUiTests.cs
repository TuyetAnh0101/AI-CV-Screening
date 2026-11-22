extern alias web;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
using RecoveryNotice = SmartCv.Core.Contracts.AccountNotice;

namespace SmartCv.Tests;

/// <summary>B54: test logic frontend và contract HTTP. Test SQL thật của A57 nằm ở A57SqlIntegrationTests (opt-in).</summary>
public sealed class B54PasswordResetUiTests
{
    private static ApiResult<RecoveryNotice> Ok() => ApiResult<RecoveryNotice>.Success(new("accepted"));

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    private sealed class FakeRecoveryApi : IAuthRecoveryApi
    {
        public List<string> ForgotEmails { get; } = [];
        public List<(string Token, string Password)> Resets { get; } = [];
        public Func<string, Task<ApiResult<RecoveryNotice>>> Forgot { get; set; } = _ => Task.FromResult(Ok());
        public Func<string, string, Task<ApiResult<RecoveryNotice>>> Reset { get; set; } = (_, _) => Task.FromResult(Ok());
        public async Task<ApiResult<RecoveryNotice>> ForgotPasswordAsync(string email, CancellationToken ct = default)
        {
            ForgotEmails.Add(email);
            return await Forgot(email);
        }
        public async Task<ApiResult<RecoveryNotice>> ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default)
        {
            Resets.Add((token, newPassword));
            return await Reset(token, newPassword);
        }
    }

    private static ResetPasswordFlow Form(Clock clock) => new(clock) { NewPassword = "MatKhauMoi2026", ConfirmPassword = "MatKhauMoi2026" };
    private static string Token => new('A', 43);

    [Fact]
    public async Task Forgot_normalizes_email_and_uses_generic_response_without_disclosing_account()
    {
        var clock = new Clock(); var api = new FakeRecoveryApi();
        var form = new ForgotPasswordFlow(clock) { Email = " TEST@EXAMPLE.COM " };
        Assert.Equal(ForgotPasswordOutcome.Accepted, await form.SubmitAsync(api));
        Assert.Equal("test@example.com", Assert.Single(api.ForgotEmails));
        Assert.True(form.Accepted);
        Assert.Contains("Nếu email có tài khoản", ForgotPasswordFlow.GenericAccepted);
        Assert.Empty(form.Email);
        Assert.Equal(60, form.RetrySecondsLeft);
        Assert.False(form.CanSubmit);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(form.CanSubmit);
    }

    [Fact]
    public async Task Forgot_invalid_email_does_not_call_api()
    {
        var api = new FakeRecoveryApi(); var form = new ForgotPasswordFlow(new Clock()) { Email = "bad@@@" };
        Assert.Equal(ForgotPasswordOutcome.NotSent, await form.SubmitAsync(api));
        Assert.NotNull(form.EmailError);
        Assert.Empty(api.ForgotEmails);
    }

    [Fact]
    public async Task Forgot_concurrent_submit_calls_api_only_once()
    {
        var gate = new TaskCompletionSource<ApiResult<RecoveryNotice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeRecoveryApi { Forgot = _ => gate.Task };
        var form = new ForgotPasswordFlow(new Clock()) { Email = "a@example.com" };
        var first = form.SubmitAsync(api);
        Assert.True(form.Submitting);
        Assert.Equal(ForgotPasswordOutcome.NotSent, await form.SubmitAsync(api));
        gate.SetResult(Ok());
        Assert.Equal(ForgotPasswordOutcome.Accepted, await first);
        Assert.Single(api.ForgotEmails);
    }

    [Fact]
    public async Task Forgot_rate_limit_respects_retry_after_without_email_disclosure()
    {
        var clock = new Clock();
        var api = new FakeRecoveryApi
        {
            Forgot = _ => Task.FromResult(ApiResult<RecoveryNotice>.Failure("private", 429, "public_auth_rate_limited", TimeSpan.FromSeconds(125)))
        };
        var form = new ForgotPasswordFlow(clock) { Email = "x@example.com" };
        Assert.Equal(ForgotPasswordOutcome.RateLimited, await form.SubmitAsync(api));
        Assert.Equal(125, form.RetrySecondsLeft);
        Assert.DoesNotContain("private", form.FormError);
        Assert.False(form.Accepted);
        Assert.Equal(ForgotPasswordOutcome.NotSent, await form.SubmitAsync(api));
    }

    [Fact]
    public async Task Forgot_network_failure_is_not_claimed_success_and_has_cooldown()
    {
        var api = new FakeRecoveryApi { Forgot = _ => Task.FromResult(ApiResult<RecoveryNotice>.LocalFailure("offline", ApiFailureKind.Network)) };
        var form = new ForgotPasswordFlow(new Clock()) { Email = "x@example.com" };
        Assert.Equal(ForgotPasswordOutcome.Failed, await form.SubmitAsync(api));
        Assert.False(form.Accepted);
        Assert.False(form.CanSubmit);
        Assert.Contains("Có thể thư đã được gửi", form.FormError);
    }

    [Theory]
    [InlineData("ngan1", "ngan1")]
    [InlineData("chichu", "chichu")]
    [InlineData(" MatKhauMoi2026", " MatKhauMoi2026")]
    [InlineData("MatKhauMoi2026", "KhongKhop2026")]
    public async Task Reset_client_validation_never_sends_invalid_password(string password, string confirm)
    {
        var api = new FakeRecoveryApi();
        var form = new ResetPasswordFlow(new Clock()) { NewPassword = password, ConfirmPassword = confirm };
        Assert.Equal(ResetPasswordOutcome.NotSent, await form.SubmitAsync(api, Token));
        Assert.Empty(api.Resets);
        Assert.True(form.NewError is not null || form.ConfirmError is not null);
    }

    [Fact]
    public async Task Reset_success_sends_untrimmed_password_once_and_clears_secret()
    {
        var form = Form(new Clock()); var api = new FakeRecoveryApi();
        Assert.Equal(ResetPasswordOutcome.Changed, await form.SubmitAsync(api, Token));
        Assert.Equal((Token, "MatKhauMoi2026"), Assert.Single(api.Resets));
        Assert.True(form.Completed);
        Assert.Empty(form.NewPassword);
        Assert.Empty(form.ConfirmPassword);
        Assert.Equal(ResetPasswordOutcome.NotSent, await form.SubmitAsync(api, Token));
    }

    [Theory]
    [InlineData("token_expired", ResetPasswordOutcome.Expired)]
    [InlineData("token_invalid", ResetPasswordOutcome.Invalid)]
    public async Task Reset_terminal_token_errors_disable_retry(string code, ResetPasswordOutcome outcome)
    {
        var api = new FakeRecoveryApi { Reset = (_, _) => Task.FromResult(ApiResult<RecoveryNotice>.Failure("raw", 400, code)) };
        var form = Form(new Clock());
        Assert.Equal(outcome, await form.SubmitAsync(api, Token));
        Assert.False(form.CanSubmit);
        Assert.DoesNotContain("raw", form.FormError);
        Assert.Empty(form.NewPassword);
    }

    [Fact]
    public async Task Reset_server_password_validation_is_displayed_on_new_password_field()
    {
        var api = new FakeRecoveryApi
        {
            Reset = (_, _) => Task.FromResult(ApiResult<RecoveryNotice>.Failure("raw", 400, "validation_failed", null,
                new Dictionary<string, string[]> { ["newPassword"] = ["Mật khẩu mới không được trùng email."] }))
        };
        var form = Form(new Clock());
        Assert.Equal(ResetPasswordOutcome.Failed, await form.SubmitAsync(api, Token));
        Assert.Equal("Mật khẩu mới không được trùng email.", form.NewError);
        Assert.True(form.CanSubmit);
    }

    [Fact]
    public async Task Reset_rate_limit_waits_without_sending_more_requests()
    {
        var clock = new Clock();
        var api = new FakeRecoveryApi { Reset = (_, _) => Task.FromResult(ApiResult<RecoveryNotice>.Failure("raw", 429, "public_auth_rate_limited", TimeSpan.FromMinutes(5))) };
        var form = Form(clock);
        Assert.Equal(ResetPasswordOutcome.RateLimited, await form.SubmitAsync(api, Token));
        Assert.Equal(300, form.RetrySecondsLeft);
        Assert.Equal(ResetPasswordOutcome.NotSent, await form.SubmitAsync(api, Token));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(form.CanSubmit);
    }

    [Fact]
    public async Task Reset_unknown_result_never_auto_retries_or_keeps_passwords()
    {
        var api = new FakeRecoveryApi { Reset = (_, _) => Task.FromResult(ApiResult<RecoveryNotice>.LocalFailure("offline", ApiFailureKind.Timeout)) };
        var form = Form(new Clock());
        Assert.Equal(ResetPasswordOutcome.Unknown, await form.SubmitAsync(api, Token));
        Assert.True(form.Unresolved);
        Assert.False(form.CanSubmit);
        Assert.Empty(form.NewPassword);
        Assert.Single(api.Resets);
    }

    [Fact]
    public async Task Malformed_token_never_reaches_api()
    {
        var api = new FakeRecoveryApi(); var form = Form(new Clock());
        Assert.Equal(ResetPasswordOutcome.Invalid, await form.SubmitAsync(api, "not-a-token"));
        Assert.Empty(api.Resets);
        Assert.False(form.CanSubmit);
    }

    [Fact]
    public async Task Reset_duplicate_click_while_pending_has_single_write()
    {
        var gate = new TaskCompletionSource<ApiResult<RecoveryNotice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeRecoveryApi { Reset = (_, _) => gate.Task };
        var form = Form(new Clock());
        var first = form.SubmitAsync(api, Token);
        Assert.True(form.Submitting);
        Assert.Equal(ResetPasswordOutcome.NotSent, await form.SubmitAsync(api, Token));
        gate.SetResult(Ok());
        Assert.Equal(ResetPasswordOutcome.Changed, await first);
        Assert.Single(api.Resets);
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }

    [Fact]
    public async Task Http_contract_uses_post_json_body_and_never_places_token_or_bearer_in_url()
    {
        var paths = new List<string>();
        using var handler = new CaptureHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Null(request.Headers.Authorization);
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            var payload = await request.Content!.ReadAsStringAsync();
            if (path.EndsWith("reset-password", StringComparison.Ordinal))
            {
                Assert.Contains(Token, payload);
                Assert.Contains("MatKhauMoi2026", payload);
                Assert.DoesNotContain(Token, request.RequestUri.ToString());
            }
            else Assert.Contains("test@example.com", payload);
            return new HttpResponseMessage(path.EndsWith("forgot-password", StringComparison.Ordinal) ? HttpStatusCode.Accepted : HttpStatusCode.OK)
            { Content = new StringContent("{\"message\":\"accepted\"}", Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        var api = new AuthApiClient(client, NullLogger<AuthApiClient>.Instance);
        Assert.True((await api.ForgotPasswordAsync("test@example.com")).IsSuccess);
        Assert.True((await api.ResetPasswordAsync(Token, "MatKhauMoi2026")).IsSuccess);
        Assert.Equal(["/api/auth/forgot-password", "/api/auth/reset-password"], paths);
    }

    [Fact]
    public async Task Existing_tab_reacts_to_revoked_jwt_with_401()
    {
        await using var tab = await B28Tab.SignedInAsync();
        var result = await tab.RevokeAsync();
        Assert.Equal(401, result.StatusCode);
        Assert.Equal(AuthEndReason.Expired, tab.Session.EndReason);
    }
}
