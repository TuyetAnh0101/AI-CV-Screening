using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;

namespace SmartCv.Tests.Auth;

public class A30SecurityTests
{
    [Fact]
    public async Task Real_Kestrel_HTTP_create_login_force_change_and_new_token_work_without_real_accounts_or_SQL()
    {
        using var factory = A28TestSupport.Factory(); factory.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var password = A28TestSupport.Password(); var admin = await A28TestSupport.Seed(factory, password);
        using var http = factory.CreateClient();
        using var login = await http.PostAsJsonAsync("/api/auth/login", new { email = admin.Email, password }); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = (await login.Content.ReadFromJsonAsync<LoginResponse>())!; http.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
        var created = await A30AccountEndpointTests.Created(http);
        using var hr = factory.CreateClient(); using var signIn = await hr.PostAsJsonAsync("/api/auth/login", new { email = created.User.Email, password = created.TemporaryPassword });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode); var temporary = (await signIn.Content.ReadFromJsonAsync<LoginResponse>())!;
        hr.DefaultRequestHeaders.Authorization = new("Bearer", temporary.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.GetAsync("/api/jobs")).StatusCode);
        using var change = await hr.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = created.TemporaryPassword, newPassword = A28TestSupport.Password() });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode); var changed = (await change.Content.ReadFromJsonAsync<ChangePasswordResponse>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await hr.GetAsync("/api/auth/me")).StatusCode);
        hr.DefaultRequestHeaders.Authorization = new("Bearer", changed.Token);
        Assert.Equal(HttpStatusCode.OK, (await hr.GetAsync("/api/jobs")).StatusCode);
    }

    [Fact]
    public async Task Account_logs_never_contain_passwords_hashes_stamps_or_issued_tokens()
    {
        var logs = new A28TestSupport.LogCapture(); using var factory = A28TestSupport.Factory(logs: logs);
        var adminPassword = A28TestSupport.Password(); var admin = await A28TestSupport.Seed(factory, adminPassword);
        using var manager = A30AccountEndpointTests.Client(factory, admin); var created = await A30AccountEndpointTests.Created(manager);
        using var http = factory.CreateClient();
        using var login = await http.PostAsJsonAsync("/api/auth/login", new { email = created.User.Email, password = created.TemporaryPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode); var session = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        http.DefaultRequestHeaders.Authorization = new("Bearer", session.Token); var next = A28TestSupport.Password();
        using var changed = await http.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = created.TemporaryPassword, newPassword = next }); Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var response = (await changed.Content.ReadFromJsonAsync<ChangePasswordResponse>())!; var stored = await A30AccountEndpointTests.Saved(factory, created.User.UserId);
        var text = string.Join('\n', logs.Messages);
        foreach (var secret in new[] { adminPassword, created.TemporaryPassword!, next, stored.PasswordHash, stored.SecurityStamp, session.Token, response.Token, A28TestSupport.Key })
            Assert.True(!text.Contains(secret, StringComparison.Ordinal), "A credential or token was logged.");
        Assert.Contains("change-password", text);
    }

    [Fact]
    public async Task Corrupt_password_hash_is_a_safe_service_error_and_does_not_mutate_the_account()
    {
        using var factory = A28TestSupport.Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        await A28TestSupport.WithDb(factory, async db => { var current = await db.Users.SingleAsync(); current.PasswordHash = "not-base64"; await db.SaveChangesAsync(); });
        var before = A30AccountEndpointTests.Fingerprint(await A30AccountEndpointTests.Saved(factory, user.UserId)); using var http = A30AccountEndpointTests.Client(factory, user);
        using var response = await http.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = A28TestSupport.Password(), newPassword = A28TestSupport.Password() });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(before == A30AccountEndpointTests.Fingerprint(await A30AccountEndpointTests.Saved(factory, user.UserId)));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(129)]
    public void Temporary_password_generator_rejects_unbounded_lengths(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => UserRules.GenerateTemporaryPassword(length));

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=201")]
    [InlineData("page=2147483647&pageSize=200")]
    [InlineData("isActive=invalid")]
    [InlineData("role=Manager")]
    [InlineData("role=Admin&role=HR")]
    public async Task Invalid_list_filters_are_field_errors_without_mutating_accounts(string query)
    {
        using var factory = A28TestSupport.Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password()); using var http = A30AccountEndpointTests.Client(factory, admin);
        var before = A30AccountEndpointTests.Fingerprint(await A30AccountEndpointTests.Saved(factory, admin.UserId));
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/users?" + query)).StatusCode);
        Assert.True(before == A30AccountEndpointTests.Fingerprint(await A30AccountEndpointTests.Saved(factory, admin.UserId)));
    }
}