extern alias web;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

public sealed class B53RegisterUiTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    private sealed class FakeRegistrationApi : IRegistrationApi
    {
        public int RegisterCalls { get; private set; }
        public int ResendCalls { get; private set; }
        public Func<RegisterRequest, Task<ApiResult<RegistrationNotice>>> Register =
            _ => Task.FromResult(ApiResult<RegistrationNotice>.Success(new RegistrationNotice("accepted")));
        public Func<string, Task<ApiResult<RegistrationNotice>>> Resend =
            _ => Task.FromResult(ApiResult<RegistrationNotice>.Success(new RegistrationNotice("accepted")));
        public Task<ApiResult<RegistrationSettingsResponse>> GetRegistrationSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult(ApiResult<RegistrationSettingsResponse>.Success(new(true, true, [], new(8, 128, true, true), "2026-11")));
        public async Task<ApiResult<RegistrationNotice>> RegisterAsync(RegisterRequest input, CancellationToken ct = default)
        {
            RegisterCalls++;
            return await Register(input);
        }
        public async Task<ApiResult<RegistrationNotice>> ResendVerificationAsync(string email, CancellationToken ct = default)
        {
            ResendCalls++;
            return await Resend(email);
        }
        public Task<ApiResult<LoginResponse>> VerifyEmailAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<LoginResponse>.Failure("x", 400, "token_invalid"));
    }

    private static RegisterForm ValidForm()
    {
        return new RegisterForm(Clock)
        {
            FullName = "Nguyễn Thị B",
            Email = " TEST@EXAMPLE.COM ", Password = "StrongPass123", ConfirmPassword = "StrongPass123", AcceptTerms = true
        };
    }

    [Fact]
    public void Validation_is_shared_with_A56_and_checks_confirmation()
    {
        var form = ValidForm();
        form.ConfirmPassword = "WrongPass123";
        Assert.False(form.Validate());
        Assert.NotNull(form.ConfirmPasswordError);
        Assert.Equal(RegisterField.ConfirmPassword, form.FocusTarget);
        form.ConfirmPassword = form.Password;
        form.AcceptTerms = false;
        Assert.False(form.Validate());
        Assert.Equal(RegisterField.AcceptTerms, form.FocusTarget);
        form.AcceptTerms = true;
        Assert.True(form.Validate());
        Assert.Equal("test@example.com", form.NormalizedEmail);
    }

    [Fact]
    public async Task Successful_registration_does_not_presume_email_exists_and_clears_secrets()
    {
        var form = ValidForm(); var api = new FakeRegistrationApi();
        Assert.Equal(RegisterOutcome.Accepted, await form.SubmitAsync(api));
        Assert.True(form.Succeeded);
        Assert.Equal(1, api.RegisterCalls);
        Assert.Equal("", form.Password);
        Assert.Equal("", form.ConfirmPassword);
        Assert.False(form.CanSubmit);
    }

    [Fact]
    public async Task Repeated_submit_while_api_pending_never_sends_twice()
    {
        var gate = new TaskCompletionSource<ApiResult<RegistrationNotice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeRegistrationApi { Register = _ => gate.Task };
        var form = ValidForm();
        var running = form.SubmitAsync(api);
        Assert.True(form.Submitting);
        Assert.Equal(RegisterOutcome.NotSent, await form.SubmitAsync(api));
        gate.SetResult(ApiResult<RegistrationNotice>.Success(new RegistrationNotice("accepted")));
        Assert.Equal(RegisterOutcome.Accepted, await running);
        Assert.Equal(1, api.RegisterCalls);
    }

    [Fact]
    public async Task Field_errors_are_mapped_and_password_is_preserved()
    {
        var api = new FakeRegistrationApi
        {
            Register = _ => Task.FromResult(ApiResult<RegistrationNotice>.Failure("invalid", 400, "invalid_request", null,
                new Dictionary<string, string[]> { ["email"] = ["Email đã nhập không hợp lệ."] }))
        };
        var form = ValidForm();
        Assert.Equal(RegisterOutcome.Invalid, await form.SubmitAsync(api));
        Assert.Equal("Email đã nhập không hợp lệ.", form.EmailError);
        Assert.Equal("StrongPass123", form.Password);
        Assert.Equal(RegisterField.Email, form.FocusTarget);
    }

    [Fact]
    public async Task Disabled_feature_and_retry_after_prevent_requests()
    {
        var api = new FakeRegistrationApi
        {
            Register = _ => Task.FromResult(ApiResult<RegistrationNotice>.Failure("too many", 429, "registration_rate_limited", TimeSpan.FromSeconds(15)))
        };
        var form = ValidForm();
        Assert.Equal(RegisterOutcome.RateLimited, await form.SubmitAsync(api));
        Assert.True(form.IsRateLimited);
        Assert.Equal(RegisterOutcome.NotSent, await form.SubmitAsync(api));
        Assert.Equal(1, api.RegisterCalls);
        var off = ValidForm();
        api.Register = _ => Task.FromResult(ApiResult<RegistrationNotice>.Failure("off", 404, "registration_disabled"));
        Assert.Equal(RegisterOutcome.Disabled, await off.SubmitAsync(api));
        Assert.False(off.CanSubmit);
    }

    [Fact]
    public void Masked_email_never_puts_complete_address_in_url()
    {
        var email = "private.longname@example.org";
        var masked = RegistrationFlowState.Mask(email);
        Assert.Equal("p***@example.org", masked);
        Assert.DoesNotContain("private.longname", masked);
        Assert.Equal(masked, RegistrationFlowState.SafeMaskedDisplay(masked));
        Assert.Empty(RegistrationFlowState.SafeMaskedDisplay("private.longname@example.org"));
        Assert.Empty(RegistrationFlowState.SafeMaskedDisplay("<b>x***@site.com</b>"));
    }

    [Theory]
    [InlineData(400, "token_expired", VerifyOutcome.Expired)]
    [InlineData(400, "token_invalid", VerifyOutcome.Invalid)]
    [InlineData(503, "workspace_unavailable", VerifyOutcome.Retryable)]
    [InlineData(429, "verify_rate_limited", VerifyOutcome.RateLimited)]
    public void Verify_classifies_error_codes_without_server_text(int status, string code, VerifyOutcome expected)
    {
        var result = ApiResult<LoginResponse>.Failure("SOME ENGLISH TEXT", status, code);
        Assert.Equal(expected, VerificationResultRules.Classify(result));
    }

    [Fact]
    public async Task Resend_has_generic_success_message_and_blocks_second_request()
    {
        var state = new RegistrationFlowState(Clock);
        state.Remember("Test@EXAMPLE.com");
        var form = new ResendVerificationFlow(Clock, state);
        var api = new FakeRegistrationApi();
        Assert.Equal(ResendOutcome.Accepted, await form.SubmitAsync(api));
        Assert.Equal(1, api.ResendCalls);
        Assert.False(form.CanSubmit);
        Assert.Equal(ResendOutcome.NotSent, await form.SubmitAsync(api));
    }

    [Fact]
    public void Not_verified_login_is_specific_without_weakening_invalid_credentials()
    {
        var denied = ApiResult<LoginResponse>.Failure("secret", 403, "email_not_verified");
        Assert.Contains("xác minh", AuthText.LoginError(denied), StringComparison.OrdinalIgnoreCase);
        var unknown = ApiResult<LoginResponse>.Failure("secret", 401, "invalid_credentials");
        Assert.Equal(AuthText.InvalidCredentials, AuthText.LoginError(unknown));
    }

    [Fact]
    public async Task Http_client_uses_public_endpoints_without_bearer()
    {
        var calls = new List<(string Method, string Path, bool Authorization)>();
        var handler = new FakeHttpHandler(req =>
        {
            calls.Add((req.Method.Method, req.RequestUri!.AbsolutePath, req.Headers.Authorization is not null));
            var response = req.RequestUri.AbsolutePath.EndsWith("registration-settings", StringComparison.Ordinal)
                ? "{\"registrationEnabled\":true,\"emailPasswordEnabled\":true,\"providers\":[],\"passwordRules\":{\"minLength\":8,\"maxLength\":128,\"requiresLetter\":true,\"requiresDigit\":true},\"termsVersion\":\"2026-11\"}"
                : req.RequestUri.AbsolutePath.EndsWith("verify-email", StringComparison.Ordinal)
                    ? "{\"token\":\"signed-token\",\"expiresAt\":\"2030-01-01T00:00:00Z\",\"user\":{\"userId\":2,\"email\":\"a@b.com\",\"fullName\":\"A\",\"role\":\"HR\"}}"
                    : "{\"message\":\"accepted\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json") };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:7001/") };
        var api = new AuthApiClient(http, NullLogger<AuthApiClient>.Instance);
        Assert.True((await api.GetRegistrationSettingsAsync()).IsSuccess);
        Assert.True((await api.RegisterAsync(new RegisterRequest { FullName = "John", Email = "a@b.com", Password = "Pass1234", AcceptTerms = true })).IsSuccess);
        Assert.True((await api.ResendVerificationAsync("a@b.com")).IsSuccess);
        Assert.True((await api.VerifyEmailAsync("one-time-token")).IsSuccess);
        Assert.Equal(4, calls.Count);
        Assert.All(calls, c => Assert.False(c.Authorization));
        Assert.Equal("GET", calls[0].Method);
        Assert.Equal("/api/auth/verify-email", calls[3].Path);
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
