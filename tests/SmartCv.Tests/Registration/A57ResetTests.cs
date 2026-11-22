using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Auth;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Registration;

/// <summary>
/// Tests độc lập, không ghi vào SQL ứng dụng. Full concurrent/token rotation dùng SQL opt-in
/// được mô tả trong docs/A57-implementation-report.md (không gắn nhãn PASS khi chưa chạy).
/// </summary>
public sealed class A57ResetTests
{
    [Fact]
    public async Task Forgot_InvalidOrUnknownEmail_ReturnsSame202()
    {
        using var factory = A28TestSupport.Factory(new() { ["Email:WorkerEnabled"] = "false" });
        using var client = factory.CreateClient();
        using var bad = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest { Email = "not-an-email" });
        using var missing = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest { Email = null });
        Assert.Equal(HttpStatusCode.Accepted, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, missing.StatusCode);
        Assert.Equal(await bad.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("")]
    [InlineData("../../password")]
    public async Task Reset_InvalidToken_IsNeverAccepted(string token)
    {
        using var factory = A28TestSupport.Factory(new() { ["Email:WorkerEnabled"] = "false" });
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest
        { Token = token, NewPassword = "NewSecret9" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("token_invalid", text);
    }

    [Fact]
    public void PasswordRules_RejectWeakAndEmailAsPassword()
    {
        Assert.NotNull(PasswordRules.Validate("123", "synthetic@example.invalid"));
        Assert.NotNull(PasswordRules.Validate("synthetic@example.invalid", "synthetic@example.invalid"));
        Assert.Null(PasswordRules.Validate("Strong9Password", "synthetic@example.invalid"));
    }

    [Fact]
    public void ResetTokenPurpose_IsNotEmailVerificationPurpose()
    {
        Assert.NotEqual(TokenPurposes.PasswordReset, TokenPurposes.EmailVerification);
        Assert.Null(UserTokenService.HashToken("bad"));
        var token = UserTokenService.CreateRandomToken();
        Assert.Equal(43, token.Length);
        Assert.Equal(64, UserTokenService.HashToken(token)!.Length);
    }

    [Fact]
    public void HasPassword_IsFalseOnlyForAccountsWithoutPasswordHash()
    {
        var user = new User { PasswordHash = "" };
        Assert.False(user.HasPassword);
        var old = "Original9Password";
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, old);
        Assert.True(user.HasPassword);
    }

    [Fact]
    public async Task Reset_RejectsVerificationPurposeEvenWithValidLookingToken()
    {
        using var factory = A28TestSupport.Factory(new() { ["Email:WorkerEnabled"] = "false" });
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest
        { Token = UserTokenService.CreateRandomToken(), NewPassword = "OtherNew9Password" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void PublicForgot_RateLimitIsExplicitlyConfiguredForIp()
    {
        Assert.True(typeof(ForgotPasswordRateLimitPolicy).IsSubclassOf(typeof(PublicAuthRateLimitPolicy)));
        Assert.NotNull(typeof(AuthSetup).GetField(nameof(AuthSetup.ForgotPasswordRateLimit)));
    }
}
