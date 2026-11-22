using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Api.Auth;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.ExternalAuth;

/// <summary>Fake Google principal only; does NOT replace the real OIDC signature/nonce browser test.</summary>
public sealed class A58RegisterServiceTests
{
    private static ClaimsPrincipal Google(string email, string sub = "synthetic-google-sub") =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("sub", sub), new Claim("email", email),
            new Claim("email_verified", "true"), new Claim("name", "Nguyễn Thị Google")
        }, "google"));

    private static ExternalLoginService Service(IServiceProvider services)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Google:Enabled"] = "true",
            ["App:WebBaseUrl"] = "https://localhost:7258"
        }).Build();
        return ActivatorUtilities.CreateInstance<ExternalLoginService>(services, config);
    }

    [Fact]
    public async Task FirstLoginRegistersHrAndWorkspace_SecondLoginUsesSameUser()
    {
        using var factory = A28TestSupport.Factory(new()
        {
            ["Authentication:Google:Enabled"] = "false", ["Registration:Enabled"] = "true", ["Email:WorkerEnabled"] = "false"
        });
        using var scope = factory.Services.CreateScope();
        var service = Service(scope.ServiceProvider);
        var principal = Google("new-google-a58@example.invalid");
        var first = await service.CompleteAsync(principal, "google", "login", null, "/dashboard", default);
        Assert.Null(first.FailureCode);
        Assert.Contains("/auth/callback#code=", first.RedirectUrl);
        var raw = first.RedirectUrl.Split("#code=")[1].Split('&')[0];
        var login = await service.ExchangeAsync(raw, default);
        Assert.Equal("HR", login.Login.User.Role);
        Assert.NotNull(login.Login.User.Workspace);
        var id = login.Login.User.UserId;

        var second = await service.CompleteAsync(principal, "google", "login", null, "/jobs", default);
        Assert.Null(second.FailureCode);
        var code = second.RedirectUrl.Split("#code=")[1].Split('&')[0];
        var another = await service.ExchangeAsync(code, default);
        Assert.Equal(id, another.Login.User.UserId);
        Assert.Equal("/jobs", another.ReturnUrl);

        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(x => x.Email == "new-google-a58@example.invalid"));
        Assert.Equal(1, await db.UserExternalLogins.CountAsync(x => x.UserId == id && x.Provider == "google"));
    }

    [Fact]
    public async Task MatchingExistingEmailIsNeverLinked()
    {
        using var factory = A28TestSupport.Factory(new()
        {
            ["Authentication:Google:Enabled"] = "false", ["Registration:Enabled"] = "true", ["Email:WorkerEnabled"] = "false"
        });
        var existing = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var scope = factory.Services.CreateScope();
        var service = Service(scope.ServiceProvider);
        var result = await service.CompleteAsync(Google(existing.Email), "google", "login", null, "/dashboard", default);
        Assert.Equal("link_required", result.FailureCode);
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.False(await db.UserExternalLogins.AnyAsync(x => x.UserId == existing.UserId));
    }
}
