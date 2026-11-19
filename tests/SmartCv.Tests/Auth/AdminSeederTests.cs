using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Auth;

public class AdminSeederTests
{
    private static Task Seed(SmartCvDbContext db, SeedAdminOptions? options, CaptureLogger logger) =>
        AdminSeeder.SeedAdminAsync(db, new PasswordHasher<User>(), options, logger);

    [Fact]
    public async Task Missing_config_warns_without_creating_any_default_account()
    {
        await using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString());
        var log = new CaptureLogger();
        await Seed(db, null, log);
        Assert.Empty(db.Users);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Chưa có"));
    }

    [Fact]
    public async Task Existing_active_admin_is_unchanged_even_with_invalid_new_config()
    {
        await using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString());
        var original = new User { Email = "original@example.invalid", FullName = "Original", Role = UserRoles.Admin, PasswordHash = "synthetic-existing-hash" };
        db.Users.Add(original); await db.SaveChangesAsync();
        var stamp = original.SecurityStamp;
        await Seed(db, new() { Email = "bad", Password = "bad" }, new());
        db.ChangeTracker.Clear();
        var saved = await db.Users.SingleAsync();
        Assert.Equal(original.PasswordHash, saved.PasswordHash);
        Assert.Equal(stamp, saved.SecurityStamp);
        Assert.Equal(original.FullName, saved.FullName);
    }

    [Fact]
    public async Task Locked_admin_with_another_email_is_preserved_when_a_new_first_active_admin_is_created()
    {
        await using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString());
        var locked = new User { Email = "locked@example.invalid", FullName = "Locked", Role = UserRoles.Admin, IsActive = false, PasswordHash = "synthetic-existing-hash" };
        db.Add(locked); await db.SaveChangesAsync();
        var stamp = locked.SecurityStamp;
        await Seed(db, SeedAdminPlanTests.Valid(), new());
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Single(await db.Users.Where(u => u.Role == UserRoles.Admin && u.IsActive).ToListAsync());
        Assert.False(locked.IsActive); Assert.Equal(stamp, locked.SecurityStamp);
    }

    [Theory]
    [InlineData("HR", true)]
    [InlineData("HR", false)]
    [InlineData("Admin", false)]
    public async Task Existing_email_never_promotes_or_unlocks_account(string role, bool active)
    {
        await using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString());
        var seed = SeedAdminPlanTests.Valid();
        var user = new User { Email = seed.Email!, FullName = "Existing", Role = role, IsActive = active, PasswordHash = "synthetic-existing-hash" };
        db.Add(user); await db.SaveChangesAsync();
        var before = (user.PasswordHash, user.SecurityStamp);
        var log = new CaptureLogger();
        await Seed(db, seed, log);
        db.ChangeTracker.Clear();
        var saved = await db.Users.SingleAsync();
        Assert.Equal((role, active, before.PasswordHash, before.SecurityStamp), (saved.Role, saved.IsActive, saved.PasswordHash, saved.SecurityStamp));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.All(log.Entries, e => Assert.DoesNotContain(seed.Password!, e.Message));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("password")]
    [InlineData("name")]
    public async Task Invalid_config_throws_without_writes_or_secret_output(string field)
    {
        await using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString());
        var seed = SeedAdminPlanTests.Valid();
        if (field == "email") seed.Email = "invalid";
        if (field == "password") seed.Password = "bad1";
        if (field == "name") seed.FullName = new string('x', 151);
        var log = new CaptureLogger();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Seed(db, seed, log));
        Assert.DoesNotContain(seed.Password!, error.ToString());
        Assert.Empty(db.Users);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Valid_admin_is_hashed_and_restart_changes_nothing()
    {
        var database = Guid.NewGuid().ToString();
        var seed = SeedAdminPlanTests.Valid();
        var log = new CaptureLogger();
        var before = DateTime.UtcNow;
        await using (var db = TestSupport.InMemoryDb(database)) await Seed(db, seed, log);
        await using var verify = TestSupport.InMemoryDb(database);
        var admin = await verify.Users.SingleAsync();
        Assert.Equal(("admin@example.invalid", "Quản trị hệ thống", UserRoles.Admin, true, false),
            (admin.Email, admin.FullName, admin.Role, admin.IsActive, admin.MustChangePassword));
        Assert.Null(admin.LastLoginAt);
        Assert.InRange(admin.CreatedAt, before, DateTime.UtcNow);
        Assert.Equal(DateTimeKind.Utc, admin.CreatedAt.Kind);
        Assert.DoesNotContain(seed.Password!, admin.PasswordHash);
        var hasher = new PasswordHasher<User>();
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(admin, admin.PasswordHash, seed.Password!));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(admin, admin.PasswordHash, seed.Password! + "wrong"));
        var original = (admin.UserId, admin.PasswordHash, admin.SecurityStamp, admin.CreatedAt);
        await using (var restarted = TestSupport.InMemoryDb(database)) await Seed(restarted, SeedAdminPlanTests.Valid("other@example.invalid"), log);
        verify.ChangeTracker.Clear();
        var saved = await verify.Users.SingleAsync();
        Assert.Equal(original, (saved.UserId, saved.PasswordHash, saved.SecurityStamp, saved.CreatedAt));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Information);
        Assert.All(log.Entries, e => Assert.DoesNotContain(seed.Password!, e.Message));
    }

    [Fact]
    public void Security_stamps_are_random_32_byte_values()
    {
        var stamps = Enumerable.Range(0, 100).Select(_ => new User().SecurityStamp).ToArray();
        Assert.Equal(stamps.Length, stamps.Distinct().Count());
        Assert.All(stamps, stamp => Assert.Matches("^[0-9A-F]{64}$", stamp));
    }

    [Fact]
    public async Task Program_binds_options_resolves_hasher_and_seeds_before_serving_requests()
    {
        var seed = SeedAdminPlanTests.Valid();
        using var factory = TestSupport.ApiFactory(settings: new()
        {
            ["Auth:SeedAdmin:Email"] = seed.Email, ["Auth:SeedAdmin:FullName"] = seed.FullName,
            ["Auth:SeedAdmin:Password"] = seed.Password
        });
        using var client = factory.CreateClient();
        Assert.True((await client.GetAsync("/api/health")).IsSuccessStatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var user = await db.Users.SingleAsync();
        Assert.Equal(UserRoles.Admin, user.Role);
        Assert.Equal(PasswordVerificationResult.Success, scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>()
            .VerifyHashedPassword(user, user.PasswordHash, seed.Password!));
    }

    internal sealed class CaptureLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
