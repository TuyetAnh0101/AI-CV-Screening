using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Auth;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.ExternalAuth;

public sealed class A58ExchangeTests
{
    [Fact]
    public async Task CodeIsOneTimeAndRetainsOnlySafeReturnUrl()
    {
        using var factory = A28TestSupport.Factory(new() { ["Authentication:Google:Enabled"] = "false", ["Email:WorkerEnabled"] = "false" });
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var raw = UserTokenService.CreateRandomToken();
        await A28TestSupport.WithDb(factory, async db =>
        {
            db.UserTokens.Add(new UserToken
            {
                UserId = user.UserId, Purpose = ExternalProviders.ExternalLoginCode,
                TokenHash = UserTokenService.HashToken(raw)!,
                CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddSeconds(60),
                Payload = "/jobs?view=grid"
            });
            await db.SaveChangesAsync();
        });
        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();
        var result = await svc.ExchangeAsync(raw, CancellationToken.None);
        Assert.NotEmpty(result.Login.Token);
        Assert.Equal(user.UserId, result.Login.User.UserId);
        Assert.Equal("/jobs?view=grid", result.ReturnUrl);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => svc.ExchangeAsync(raw, CancellationToken.None));
        Assert.Contains("không hợp lệ", error.Message);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var stored = await db.UserTokens.AsNoTracking()
                .SingleAsync(x => x.TokenHash == UserTokenService.HashToken(raw));

            Assert.NotNull(stored.ConsumedAt);
        });
    }

    [Fact]
    public async Task InvalidTokenFailsClosed()
    {
        using var factory = A28TestSupport.Factory(new() { ["Authentication:Google:Enabled"] = "false", ["Email:WorkerEnabled"] = "false" });
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();
        var err = await Assert.ThrowsAnyAsync<Exception>(() => service.ExchangeAsync("not-a-valid-token", CancellationToken.None));
        Assert.Contains("không hợp lệ", err.Message);
    }

    [Fact]
    public void RawCodeOnlyHasHashInTokenStorage()
    {
        var raw = UserTokenService.CreateRandomToken();
        var hash = UserTokenService.HashToken(raw);
        Assert.NotNull(hash);
        Assert.Equal(64, hash!.Length);
        Assert.DoesNotContain(raw, hash, StringComparison.Ordinal);
    }
}
