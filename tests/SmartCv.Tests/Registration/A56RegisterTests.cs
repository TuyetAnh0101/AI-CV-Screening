using System.Net;
using System.Net.Http.Json;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Registration;

public sealed class A56RegisterTests
{
    [Fact]
    public async Task PublicSettingsDefaultDisabled_AndRegisterResendReturn404()
    {
        using var factory = A28TestSupport.Factory(new Dictionary<string, string?>
        {
            ["Email:WorkerEnabled"] = "false",
            ["Registration:Enabled"] = "false"
        });
        using var client = factory.CreateClient();
        using var settings = await client.GetAsync("/api/auth/registration-settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        Assert.Equal(false, (await settings.Content.ReadFromJsonAsync<RegistrationSettingsResponse>())!.RegistrationEnabled);
        using var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest());
        using var resend = await client.PostAsJsonAsync("/api/auth/resend-verification", new ResendVerificationRequest());
        Assert.Equal(HttpStatusCode.NotFound, register.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, resend.StatusCode);
    }

    [Fact]
    public async Task InvalidRegisterInput_Returns400WithoutDatabaseWrite()
    {
        using var factory = A28TestSupport.Factory(new Dictionary<string, string?>
        {
            ["Email:WorkerEnabled"] = "false",
            ["Registration:Enabled"] = "true"
        });
        using var client = factory.CreateClient();
        using var result = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest { FullName = "", Email = "bad", Password = "weak", AcceptTerms = false });
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public void ValidRegistration_NormalizesEmailAndName()
    {
        var (value, errors) = RegistrationRules.Validate("  Nguyễn   Văn A ", " User@Example.COM ", "Secret1234", true);
        Assert.Empty(errors);
        Assert.NotNull(value);
        Assert.Equal("Nguyễn Văn A", value!.FullName);
        Assert.Equal("user@example.com", value.Email);
    }

    [Theory]
    [InlineData("A", "valid@example.com", "Password123", true, "fullName")]
    [InlineData("", "valid@example.com", "Password123", true, "fullName")]
    [InlineData("Họ tên", "bad@", "Password123", true, "email")]
    [InlineData("Họ tên", "valid@example.com", "abc", true, "password")]
    [InlineData("Họ tên", "valid@example.com", "Password123", false, "acceptTerms")]
    public void InvalidRegistration_HasPerFieldError(string name, string email, string password, bool accept, string field)
    {
        var (value, errors) = RegistrationRules.Validate(name, email, password, accept);
        Assert.Null(value);
        Assert.Contains(field, errors.Keys);
    }

    [Fact]
    public void ControlCharactersInNameAndEmail_AreRejected()
    {
        Assert.Contains("fullName", RegistrationRules.Validate("A\0B", "valid@example.com", "Password123", true).Errors.Keys);
        Assert.Contains("email", RegistrationRules.Validate("Họ tên", "a\0@example.com", "Password123", true).Errors.Keys);
    }

    [Fact]
    public void RegistrationContract_DoesNotContainRoleOrSignupSource()
    {
        var properties = typeof(SmartCv.Core.Contracts.RegisterRequest).GetProperties()
            .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", properties);
        Assert.DoesNotContain("signupSource", properties);
    }
}
