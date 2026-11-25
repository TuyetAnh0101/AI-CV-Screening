using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Auth;

public class A30AccountEndpointTests
{
    internal static HttpClient Client(WebApplicationFactory<Program> factory, User user) => A29AuthorizationTests.Client(factory, user);
    internal static async Task<User> Saved(WebApplicationFactory<Program> factory, int id)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.True(db.Database.IsInMemory());
        return await db.Users.AsNoTracking().SingleAsync(u => u.UserId == id);
    }
    internal static string Fingerprint(User user) => JsonSerializer.Serialize(new
    { user.UserId, user.Email, user.FullName, user.PasswordHash, user.Role, user.IsActive, user.MustChangePassword, user.SecurityStamp, user.CreatedAt, user.LastLoginAt });
    internal static Task<HttpResponseMessage> Create(HttpClient http, string? email = null, string? password = null, string role = "HR") => http.PostAsJsonAsync("/api/users", new
    { email = email ?? Guid.NewGuid().ToString("N") + "@example.invalid", fullName = "  Nhân  sự tổng hợp  ", role, password });
    internal static async Task<UserCredentialResponse> Created(HttpClient http, string? password = null)
    {
        using var response = await Create(http, password: password); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<UserCredentialResponse>())!;
    }
    private static Task<HttpResponseMessage> Login(HttpClient http, string email, string password) => http.PostAsJsonAsync("/api/auth/login", new { email, password });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admin_creates_exactly_one_account_hashes_password_and_never_changes_the_existing_Admin(bool provided)
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var before = Fingerprint(await Saved(factory, admin.UserId)); using var http = Client(factory, admin);
        var supplied = provided ? A28TestSupport.Password() : null;
        var created = await Created(http, supplied); var stored = await Saved(factory, created.User.UserId);
        Assert.Equal("Nhân sự tổng hợp", stored.FullName); Assert.True(stored.IsActive && stored.MustChangePassword);
        Assert.Equal("HR", stored.Role); Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
        Assert.Equal(provided, created.TemporaryPassword is null);
        var password = supplied ?? created.TemporaryPassword!;
        Assert.Null(PasswordRules.Validate(password, stored.Email));
        if (!provided) Assert.Equal(12, password.Length);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<User>().VerifyHashedPassword(stored, stored.PasswordHash, password));
        Assert.True(stored.PasswordHash != password && !stored.PasswordHash.Contains(password, StringComparison.Ordinal));
        Assert.True(created.User.Version != stored.SecurityStamp); Assert.Equal(64, created.User.Version.Length);
        Assert.True(before == Fingerprint(await Saved(factory, admin.UserId)), "Existing synthetic Admin must remain byte-for-byte unchanged.");
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(2, await db.Users.CountAsync()));
        using var list = await http.GetAsync("/api/users"); Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var json = await A29AuthorizationTests.Json(list);
        Assert.All(json.EnumerateArray(), item =>
        {
            Assert.False(item.TryGetProperty("passwordHash", out _)); Assert.False(item.TryGetProperty("securityStamp", out _));
            Assert.False(item.TryGetProperty("password", out _)); Assert.False(item.TryGetProperty("temporaryPassword", out _));
        });
    }

    [Fact]
    public async Task Normalized_duplicate_email_returns_409_including_locked_accounts_without_promoting_or_unlocking()
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR", active: false);
        var before = Fingerprint(await Saved(factory, hr.UserId)); using var http = Client(factory, admin);
        using var duplicate = await Create(http, "  " + hr.Email.ToUpperInvariant() + "  ", role: "Admin");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("email_conflict", (await A29AuthorizationTests.Json(duplicate)).GetProperty("code").GetString());
        Assert.True(before == Fingerprint(await Saved(factory, hr.UserId)));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("fullName")]
    [InlineData("role")]
    [InlineData("password")]
    public async Task Invalid_create_returns_field_errors_without_any_write(string field)
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password()); using var http = Client(factory, admin);
        var request = new Dictionary<string, object?> { ["email"] = "synthetic@example.invalid", ["fullName"] = "Synthetic", ["role"] = "HR", ["password"] = A28TestSupport.Password() };
        request[field] = field switch { "email" => "invalid", "fullName" => new string('x', 151), "role" => "Manager", _ => "short" };
        using var response = await http.PostAsJsonAsync("/api/users", request); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await A29AuthorizationTests.Json(response)).GetProperty("errors").TryGetProperty(field, out _));
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(1, await db.Users.CountAsync()));
    }

    [Fact]
    public async Task Temporary_password_login_change_password_and_old_JWT_invalidation_match_frontend_contract()
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password()); using var manager = Client(factory, admin);
        var created = await Created(manager); using var hr = factory.CreateClient();
        using var login = await Login(hr, created.User.Email, created.TemporaryPassword!); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var initial = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.True(initial.User.MustChangePassword); hr.DefaultRequestHeaders.Authorization = new("Bearer", initial.Token);
        Assert.Equal(HttpStatusCode.OK, (await hr.GetAsync("/api/auth/me")).StatusCode);
        using var blocked = await hr.GetAsync("/api/jobs"); Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        var blockedData = await A29AuthorizationTests.Json(blocked);
        Assert.Equal("password_change_required", blockedData.GetProperty("code").GetString()); Assert.True(blockedData.GetProperty("mustChangePassword").GetBoolean());
        var next = A28TestSupport.Password();
        using var changed = await hr.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = created.TemporaryPassword, newPassword = next, userId = admin.UserId, role = "Admin" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode); Assert.True(changed.Headers.CacheControl?.NoStore);
        var session = (await changed.Content.ReadFromJsonAsync<ChangePasswordResponse>())!;
        Assert.Equal(created.User.UserId, session.User.UserId); Assert.False(session.User.MustChangePassword); Assert.Equal("HR", session.User.Role);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hr.GetAsync("/api/auth/me")).StatusCode);
        hr.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
        Assert.Equal(HttpStatusCode.OK, (await hr.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(factory.CreateClient(), created.User.Email, created.TemporaryPassword!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(factory.CreateClient(), created.User.Email, next)).StatusCode);
        var saved = await Saved(factory, created.User.UserId);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<User>().VerifyHashedPassword(saved, saved.PasswordHash, next));
    }

    [Theory]
    [InlineData("wrong-current")]
    [InlineData("weak-new")]
    [InlineData("same")]
    [InlineData("missing-current")]
    [InlineData("long-current")]
    public async Task Password_errors_do_not_change_hash_stamp_or_MustChangePassword(string kind)
    {
        using var factory = A28TestSupport.Factory(); var password = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(factory, password, "HR", mustChange: true); using var http = Client(factory, user);
        var before = Fingerprint(await Saved(factory, user.UserId));
        var current = kind switch { "wrong-current" => A28TestSupport.Password(), "missing-current" => null, "long-current" => new string('x', 129), _ => password };
        var next = kind switch { "weak-new" => "short", "same" => password, _ => A28TestSupport.Password() };
        using var response = await http.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = current, newPassword = next });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(before == Fingerprint(await Saved(factory, user.UserId)));
    }

    [Fact]
    public async Task Lock_unlock_reset_and_repeated_lock_preserve_password_or_revoke_it_as_specified()
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var password = A28TestSupport.Password(); var user = await A28TestSupport.Seed(factory, password, "HR");
        using var http = Client(factory, admin); using var old = Client(factory, user);
        using var locked = await http.PostAsync($"/api/users/{user.UserId}/lock", null); Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        var first = await Saved(factory, user.UserId);
        Assert.False(first.IsActive); Assert.True(first.PasswordHash == user.PasswordHash); Assert.True(first.SecurityStamp != user.SecurityStamp);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/jobs")).StatusCode);
        using var repeat = await http.PostAsync($"/api/users/{user.UserId}/lock", null); Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.True(first.SecurityStamp == (await Saved(factory, user.UserId)).SecurityStamp);
        using var reset = await http.PostAsync($"/api/users/{user.UserId}/reset-password", null); Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var credentials = (await reset.Content.ReadFromJsonAsync<UserCredentialResponse>())!;
        var resetUser = await Saved(factory, user.UserId); Assert.False(resetUser.IsActive); Assert.True(resetUser.MustChangePassword);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<User>().VerifyHashedPassword(resetUser, resetUser.PasswordHash, credentials.TemporaryPassword!));
        using var unlocked = await http.PostAsync($"/api/users/{user.UserId}/unlock", null); Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        var final = await Saved(factory, user.UserId);
        Assert.True(final.IsActive && final.MustChangePassword); Assert.True(resetUser.PasswordHash == final.PasswordHash);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Email_is_immutable_and_role_changes_revoke_old_tokens_without_changing_password()
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR"); using var http = Client(factory, admin); using var old = Client(factory, hr);
        using var updated = await http.PutAsJsonAsync($"/api/users/{hr.UserId}", new { fullName = "Tên mới", role = "Admin", email = "FORGED@example.invalid", password = "FORGED" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var saved = await Saved(factory, hr.UserId); Assert.Equal("Admin", saved.Role); Assert.Equal(hr.Email, saved.Email);
        Assert.True(saved.PasswordHash == hr.PasswordHash && saved.SecurityStamp != hr.SecurityStamp);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/jobs")).StatusCode);
    }

    [Fact]
    public async Task Self_lock_self_demotion_Admin_reset_and_DELETE_are_refused_without_changes()
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password()); using var http = Client(factory, admin);
        var before = Fingerprint(await Saved(factory, admin.UserId));
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/api/users/{admin.UserId}/lock", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PutAsJsonAsync($"/api/users/{admin.UserId}", new { fullName = admin.FullName, role = "HR" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/api/users/{admin.UserId}/reset-password", null)).StatusCode);
        using var delete = await http.DeleteAsync($"/api/users/{admin.UserId}"); Assert.True(delete.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden); // unmapped verb: fail-closed fallback policy since #114
        Assert.True(before == Fingerprint(await Saved(factory, admin.UserId)));
    }

    [Fact]
    public async Task Failed_writes_return_safe_503_no_credentials_and_release_the_account_gate()
    {
        var fault = new A28TestSupport.DataFault(); var logs = new A28TestSupport.LogCapture(); using var factory = A28TestSupport.Factory(fault: fault, logs: logs);
        var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password()); using var http = Client(factory, admin);
        fault.FailSave = true; using var failed = await Create(http);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var raw = await failed.Content.ReadAsStringAsync(); Assert.DoesNotContain("SYNTHETIC_PRIVATE_SQL_ERROR", raw); Assert.DoesNotContain("temporaryPassword", raw);
        fault.FailSave = false; using var retry = await Create(http); Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Credential_rate_limit_is_per_authenticated_account_and_has_a_stable_429_code()
    {
        using var factory = A28TestSupport.Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR"); using var http = Client(factory, user);
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "wrong", newPassword = A28TestSupport.Password() })).StatusCode);
        using var limited = await http.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "wrong", newPassword = A28TestSupport.Password() });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal("credential_rate_limited", (await A29AuthorizationTests.Json(limited)).GetProperty("code").GetString());
        var other = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR"); using var second = Client(factory, other);
        Assert.Equal(HttpStatusCode.BadRequest, (await second.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "wrong", newPassword = A28TestSupport.Password() })).StatusCode);
    }
}