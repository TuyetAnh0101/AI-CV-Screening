using System.Net;
using System.Net.Http.Json;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Tests.Auth;
using SmartCv.Infrastructure.Auth;

namespace SmartCv.Tests.Registration;

public sealed class A56VerifyTests
{
    [Fact]
    public async Task InvalidVerificationToken_Returns400EvenWhenRegistrationDisabled()
    {
        using var factory = A28TestSupport.Factory(new Dictionary<string, string?>
        {
            ["Email:WorkerEnabled"] = "false", ["Registration:Enabled"] = "false"
        });
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest { Token = "bad" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Token_IsCryptographicallyRandomAndHasOnlyHashedRepresentation()
    {
        var first = UserTokenService.CreateRandomToken();
        var second = UserTokenService.CreateRandomToken();
        Assert.Equal(43, first.Length);
        Assert.NotEqual(first, second);
        var digest = UserTokenService.HashToken(first);
        Assert.NotNull(digest);
        Assert.Equal(64, digest!.Length);
        Assert.NotEqual(first, digest);
        Assert.Equal(digest, UserTokenService.HashToken(first));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("../../etc/passwd")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa%")]
    public void InvalidTokens_DoNotProduceHash(string raw)
    {
        Assert.Null(UserTokenService.HashToken(raw));
    }

    [Fact]
    public void VerificationPurpose_IsDistinctFromPasswordReset()
    {
        Assert.NotEqual(TokenPurposes.EmailVerification, TokenPurposes.PasswordReset);
        Assert.True(TokenPurposes.EmailVerification.Length <= 30);
    }
}
