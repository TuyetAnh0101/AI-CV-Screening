using System.Net;
using System.Net.Http.Json;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Api.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Auth;

public class A30ConcurrencyTests
{
    private static HttpRequestMessage Post(string path, string version, object? body = null) => new(HttpMethod.Post, path)
    { Content = body is null ? null : JsonContent.Create(body), Headers = { { "If-Match", "\"" + version + "\"" } } };

    [Fact]
    public async Task Concurrent_normalized_email_creates_have_one_winner_and_one_conflict()
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        using var a = A30AccountEndpointTests.Client(factory, admin); using var b = A30AccountEndpointTests.Client(factory, admin);
        var email = Guid.NewGuid().ToString("N") + "@example.invalid";
        var results = await Task.WhenAll(A30AccountEndpointTests.Create(a, email), A30AccountEndpointTests.Create(b, "  " + email.ToUpperInvariant() + "  "));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created)); Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(1, await db.Users.CountAsync(u => u.Email == email)));
        foreach (var result in results) result.Dispose();
    }

    [Fact]
    public async Task Two_resets_with_the_same_version_have_one_temporary_password_and_one_409()
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var a = A30AccountEndpointTests.Client(factory, admin); using var b = A30AccountEndpointTests.Client(factory, admin);
        var version = AuthMapping.Version(hr);
        var results = await Task.WhenAll(a.SendAsync(Post($"/api/users/{hr.UserId}/reset-password", version)), b.SendAsync(Post($"/api/users/{hr.UserId}/reset-password", version)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK)); Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var success = (await results.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<UserCredentialResponse>())!;
        var saved = await A30AccountEndpointTests.Saved(factory, hr.UserId);
        Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success, new Microsoft.AspNetCore.Identity.PasswordHasher<User>().VerifyHashedPassword(saved, saved.PasswordHash, success.TemporaryPassword!));
        foreach (var result in results) result.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_Admin_mutations_cannot_remove_all_active_Admins(bool demote)
    {
        using var factory = A28TestSupport.Factory(); var first = await A28TestSupport.Seed(factory, A28TestSupport.Password()); var second = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        using var a = A30AccountEndpointTests.Client(factory, first); using var b = A30AccountEndpointTests.Client(factory, second);
        var requests = demote
            ? new[] { a.PutAsJsonAsync($"/api/users/{second.UserId}", new { fullName = second.FullName, role = "HR" }), b.PutAsJsonAsync($"/api/users/{first.UserId}", new { fullName = first.FullName, role = "HR" }) }
            : new[] { a.PostAsync($"/api/users/{second.UserId}/lock", null), b.PostAsync($"/api/users/{first.UserId}/lock", null) };
        var results = await Task.WhenAll(requests);
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.True(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Conflict));
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(1, await db.Users.CountAsync(u => u.Role == "Admin" && u.IsActive)));
        foreach (var result in results) result.Dispose();
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("lock")]
    public async Task Personal_password_change_racing_with_Admin_action_never_overwrites_the_winner(string action)
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var oldPassword = A28TestSupport.Password(); var hr = await A28TestSupport.Seed(factory, oldPassword, "HR");
        using var management = A30AccountEndpointTests.Client(factory, admin); using var personal = A30AccountEndpointTests.Client(factory, hr);
        var version = AuthMapping.Version(hr); var newPassword = A28TestSupport.Password();
        var results = await Task.WhenAll(management.SendAsync(Post($"/api/users/{hr.UserId}/{(action == "reset" ? "reset-password" : "lock")}", version)),
            personal.SendAsync(Post("/api/auth/change-password", version, new { currentPassword = oldPassword, newPassword })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.True(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Conflict));
        var saved = await A30AccountEndpointTests.Saved(factory, hr.UserId);
        if (results[1].StatusCode == HttpStatusCode.OK)
            Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success, new Microsoft.AspNetCore.Identity.PasswordHasher<User>().VerifyHashedPassword(saved, saved.PasswordHash, newPassword));
        else if (action == "lock") { Assert.False(saved.IsActive); Assert.True(saved.PasswordHash == hr.PasswordHash); }
        else { Assert.True(saved.MustChangePassword); Assert.True(saved.PasswordHash != hr.PasswordHash); }
        foreach (var result in results) result.Dispose();
    }

    [Fact]
    public async Task Stale_body_or_header_version_cannot_overwrite_a_newer_account_update()
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR"); using var http = A30AccountEndpointTests.Client(factory, admin);
        var version = AuthMapping.Version(hr);
        using var first = await http.PutAsJsonAsync($"/api/users/{hr.UserId}", new { fullName = "First update", role = "HR", expectedVersion = version }); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var stale = await http.PutAsJsonAsync($"/api/users/{hr.UserId}", new { fullName = "Stale update", role = "Admin", expectedVersion = version }); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("First update", (await A30AccountEndpointTests.Saved(factory, hr.UserId)).FullName);
    }

    [Fact]
    public async Task Actor_disabled_while_waiting_for_the_account_lock_cannot_write_after_authentication()
    {
        var observer = new SnapshotObserver(); var name = Guid.NewGuid().ToString("N");
        using var factory = A28TestSupport.Factory().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<DbContextOptions<SmartCvDbContext>>(); s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            s.AddDbContext<SmartCvDbContext>(o => o.UseInMemoryDatabase(name).AddInterceptors(observer));
        }));
        var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password()); var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var coordinator = factory.Services.GetRequiredService<AccountWriteCoordinator>();
        await using var hold = await coordinator.BeginAsync(db, default); observer.Enabled = true;
        using var http = A30AccountEndpointTests.Client(factory, admin);
        var pending = http.PostAsync($"/api/users/{hr.UserId}/lock", null);
        await observer.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await A28TestSupport.WithDb(factory, async context => { var user = await context.Users.FindAsync(admin.UserId); user!.IsActive = false; await context.SaveChangesAsync(); });
        await hold.DisposeAsync(); using var result = await pending;
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.True((await A30AccountEndpointTests.Saved(factory, hr.UserId)).IsActive);
    }

    private sealed class SnapshotObserver : IQueryExpressionInterceptor
    {
        internal bool Enabled;
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            if (Enabled && queryExpression.Type == typeof(User) && queryExpression.ToString().Contains("SingleOrDefault", StringComparison.Ordinal)) Ready.TrySetResult();
            return queryExpression;
        }
    }
}