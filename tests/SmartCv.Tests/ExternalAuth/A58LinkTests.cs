using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Entities;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.ExternalAuth;

public sealed class A58LinkTests
{
    [Theory]
    [InlineData("github")]
    [InlineData("linkedin")]
    [InlineData("unknown")]
    public async Task Historical_unsupported_login_does_not_allow_removing_the_last_supported_method(string historicalProvider)
    {
        using var factory = A28TestSupport.Factory(new() { ["Authentication:Google:Enabled"] = "false", ["Email:WorkerEnabled"] = "false" });
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        await A28TestSupport.WithDb(factory, async db =>
        {
            var current = await db.Users.SingleAsync(u => u.UserId == user.UserId);
            current.PasswordHash = "";
            db.UserExternalLogins.AddRange(
                new UserExternalLogin { UserId = user.UserId, Provider = "google", ProviderKey = "current-sub", LinkedAt = DateTime.UtcNow },
                new UserExternalLogin { UserId = user.UserId, Provider = historicalProvider, ProviderKey = "historical-sub", LinkedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", A28TestSupport.Token(user));
        using var response = await client.DeleteAsync("/api/auth/me/logins/google");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("last_login_method", await response.Content.ReadAsStringAsync());
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Equal(2, await db.UserExternalLogins.CountAsync(x => x.UserId == user.UserId));
            Assert.Empty(await db.EmailOutbox.ToListAsync());
        });
    }

    [Fact]
    public async Task UnlinkOnlyProviderWithoutPasswordIsRejected()
    {
        using var factory = A28TestSupport.Factory(new() { ["Authentication:Google:Enabled"] = "false", ["Email:WorkerEnabled"] = "false" });
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        await A28TestSupport.WithDb(factory, async db =>
        {
            var current = await db.Users.SingleAsync(u => u.UserId == user.UserId);
            current.PasswordHash = "";
            db.UserExternalLogins.Add(new UserExternalLogin { UserId = user.UserId,
                Provider = "google", ProviderKey = "a58-sub", LinkedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        // Because the stored hash changed after seed, the principal must carry the same SecurityStamp.
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();
        var principal = Principal(user);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.UnlinkAsync(principal, "google", default));
        Assert.Contains("Hãy đặt mật khẩu", error.Message);
        await A28TestSupport.WithDb(factory, async db => Assert.Single(await db.UserExternalLogins.ToListAsync()));
    }

    [Fact]
    public async Task AdminCannotUnlinkExternalProvider()
    {
        using var factory = A28TestSupport.Factory(new() { ["Authentication:Google:Enabled"] = "false", ["Email:WorkerEnabled"] = "false" });
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "Admin");
        await A28TestSupport.WithDb(factory, async db =>
        {
            db.UserExternalLogins.Add(new UserExternalLogin { UserId = user.UserId,
                Provider = "google", ProviderKey = "admin-sub", LinkedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        using var scope = factory.Services.CreateScope();
        var err = await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<ExternalLoginService>()
            .UnlinkAsync(Principal(user), "google", default));
        Assert.Contains("Admin", err.Message);
    }

    private static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity(new[]
    {
        new Claim(SmartCvClaims.UserId, user.UserId.ToString()),
        new Claim(SmartCvClaims.Role, user.Role),
        new Claim(SmartCvClaims.Stamp, user.SecurityStamp),
        new Claim(SmartCvClaims.MustChangePassword, user.MustChangePassword ? "true" : "false")
    }, "Bearer"));
}
