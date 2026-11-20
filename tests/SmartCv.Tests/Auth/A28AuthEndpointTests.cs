using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;

namespace SmartCv.Tests.Auth;

public class A28AuthEndpointTests
{
    private static Task<HttpResponseMessage> Login(HttpClient client, string email, string? password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Real_Kestrel_HTTP_login_and_Bearer_me_work_with_synthetic_InMemory_data()
    {
        using var factory = A28TestSupport.Factory();
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        using var http = factory.CreateClient();
        var password = A28TestSupport.Password(); var user = await A28TestSupport.Seed(factory, password, "HR");
        using var login = await Login(http, user.Email, password); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var result = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        http.DefaultRequestHeaders.Authorization = new("Bearer", result.Token);
        using var me = await http.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(user.UserId, (await me.Content.ReadFromJsonAsync<MeDto>())!.UserId);
    }

    [Theory]
    [InlineData("Admin", false)]
    [InlineData("HR", false)]
    [InlineData("HR", true)]
    public async Task Real_Bearer_login_and_me_match_frontend_contract_and_update_last_login(string role, bool mustChange)
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        var password = A28TestSupport.Password(); var user = await A28TestSupport.Seed(factory, password, role, mustChange: mustChange);
        using var response = await Login(http, " " + user.Email.ToUpperInvariant() + " ", password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.Equal(role, result.User.Role); Assert.Equal(mustChange, result.User.MustChangePassword);
        Assert.Equal(user.UserId, result.User.UserId);
        Assert.NotNull(result.User.LastLoginAt); Assert.Equal(DateTimeKind.Utc, result.User.LastLoginAt!.Value.Kind);
        http.DefaultRequestHeaders.Authorization = new("Bearer", result.Token);
        using var me = await http.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var data = await Json(me);
        Assert.Equal(new[] { "email", "fullName", "lastLoginAt", "mustChangePassword", "role", "userId" }, data.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain(user.SecurityStamp, data.GetRawText()); Assert.DoesNotContain(user.PasswordHash, data.GetRawText());
        await A28TestSupport.WithDb(factory, async db => Assert.NotNull((await db.Users.SingleAsync(u => u.UserId == user.UserId)).LastLoginAt));
    }

    [Fact]
    public async Task Unknown_email_wrong_password_and_locked_wrong_password_have_identical_401()
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        var password = A28TestSupport.Password(); var active = await A28TestSupport.Seed(factory, password);
        var locked = await A28TestSupport.Seed(factory, password, active: false);
        var bodies = new List<string>();
        foreach (var email in new[] { "absent@example.invalid", active.Email, locked.Email })
        {
            using var response = await Login(http, email, "wrong-synthetic-password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            bodies.Add((await Json(response)).GetRawText());
        }
        Assert.Single(bodies.Distinct());
        using var correctLocked = await Login(http, locked.Email, password);
        Assert.Equal(HttpStatusCode.Forbidden, correctLocked.StatusCode);
        Assert.Equal("account_locked", (await Json(correctLocked)).GetProperty("code").GetString());
        await A28TestSupport.WithDb(factory, async db => Assert.All(await db.Users.ToListAsync(), u => Assert.Null(u.LastLoginAt)));
    }

    [Theory]
    [InlineData("", "some-password")]
    [InlineData("invalid-email", "some-password")]
    [InlineData("valid@example.invalid", null)]
    [InlineData("valid@example.invalid", "")]
    public async Task Invalid_request_is_a_safe_structured_400(string email, string? password)
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        using var response = await Login(http, email, password);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Json(response); Assert.Equal("invalid_request", body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("errors", out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Null_or_malformed_JSON_is_400_without_a_stack_trace(string body)
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        using var response = await http.PostAsync("/api/auth/login", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(" at SmartCv.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Overlong_inputs_are_rejected_before_hashing()
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        using var email = await Login(http, new string('x', 255) + "@example.invalid", "synthetic9");
        using var password = await Login(http, "valid@example.invalid", new string('a', 129));
        Assert.Equal(HttpStatusCode.BadRequest, email.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, password.StatusCode);
    }

    [Fact]
    public async Task Wrong_content_type_and_oversized_body_are_safe_client_errors()
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        using var wrongType = await http.PostAsync("/api/auth/login", new StringContent("synthetic", Encoding.UTF8, "text/plain"));
        using var tooLarge = await http.PostAsync("/api/auth/login", new StringContent(new string('x', 4097), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
    }

    [Fact]
    public async Task Unsupported_JSON_charset_is_415_without_a_framework_exception_body()
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = "unsupported-synthetic-charset";
        using var response = await http.PostAsync("/api/auth/login", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("invalid_content_type", (await Json(response)).GetProperty("code").GetString());
        Assert.DoesNotContain("Exception", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_limits_are_independent_for_different_remote_IPs()
    {
        using var factory = A28TestSupport.Factory(new() { ["Auth:LoginAttemptsPerMinute"] = "1" });
        _ = factory.Services;
        async Task<int> Attempt(string address)
        {
            var body = Encoding.UTF8.GetBytes("{\"email\":\"absent@example.invalid\",\"password\":\"synthetic9\"}");
            var context = await factory.Server.SendAsync(ctx =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(address);
                ctx.Request.Method = "POST"; ctx.Request.Path = "/api/auth/login";
                ctx.Request.ContentType = "application/json"; ctx.Request.ContentLength = body.Length;
                ctx.Request.Body = new MemoryStream(body);
            });
            return context.Response.StatusCode;
        }
        Assert.Equal(401, await Attempt("198.51.100.1"));
        Assert.Equal(429, await Attempt("198.51.100.1"));
        Assert.Equal(401, await Attempt("198.51.100.2"));
        Assert.Equal(429, await Attempt("::ffff:198.51.100.1"));
    }

    [Theory]
    [InlineData("Server=localhost;Database=SmartCvDb;Integrated Security=True")]
    [InlineData("Server=localhost;Database=master;Integrated Security=True")]
    [InlineData("Server=localhost;Database=SmartCv_A28_Test_designated;AttachDBFilename=C:\\synthetic.mdf;Integrated Security=True")]
    public void SQL_test_guard_rejects_application_system_or_attached_catalogs(string connection) =>
        Assert.Throws<ArgumentException>(() => A28SqlFact.Validate(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection)));

    [Fact]
    public async Task PasswordHasher_rehash_is_persisted_without_rotating_security_stamp()
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        var password = A28TestSupport.Password(); var user = await A28TestSupport.Seed(factory, password, oldHash: true);
        using var response = await Login(http, user.Email, password); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var saved = await db.Users.SingleAsync(u => u.UserId == user.UserId);
            Assert.NotEqual(user.PasswordHash, saved.PasswordHash); Assert.Equal(user.SecurityStamp, saved.SecurityStamp);
            Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success,
                new Microsoft.AspNetCore.Identity.PasswordHasher<User>().VerifyHashedPassword(saved, saved.PasswordHash, password));
        });
    }

    [Fact]
    public async Task Me_requires_Bearer_and_returns_current_name_and_role()
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        using var anonymous = await http.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Bearer", anonymous.Headers.WwwAuthenticate.Single().Scheme);
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        http.DefaultRequestHeaders.Authorization = new("Bearer", A28TestSupport.Token(user));
        await A28TestSupport.WithDb(factory, async db => { var stored = await db.Users.SingleAsync(); stored.FullName = "Updated synthetic"; stored.Role = "HR"; await db.SaveChangesAsync(); });
        using var me = await http.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode); // A29 rejects stale role claims.
        user.Role = "HR";
        http.DefaultRequestHeaders.Authorization = new("Bearer", A28TestSupport.Token(user));
        using var freshMe = await http.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, freshMe.StatusCode);
        var dto = (await freshMe.Content.ReadFromJsonAsync<MeDto>())!;
        Assert.Equal(("Updated synthetic", "HR"), (dto.FullName, dto.Role));
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("deleted")]
    [InlineData("stamp")]
    public async Task Me_rechecks_account_even_when_the_session_cache_was_primed(string change)
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        http.DefaultRequestHeaders.Authorization = new("Bearer", A28TestSupport.Token(user));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/auth/me")).StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var stored = await db.Users.SingleAsync();
            if (change == "locked") stored.IsActive = false;
            if (change == "deleted") db.Users.Remove(stored);
            if (change == "stamp") stored.SecurityStamp = User.NewStamp();
            await db.SaveChangesAsync();
        });
        using var me = await http.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("expired")]
    [InlineData("wrong-key")]
    public async Task Real_authentication_middleware_rejects_bad_Bearer_tokens(string kind)
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var token = kind == "invalid" ? "invalid.jwt.value" : kind == "expired"
            ? A28TestSupport.Token(user, new A28TestSupport.Clock(DateTimeOffset.UtcNow.AddHours(-9)))
            : A28TestSupport.Token(user, settings: A28TestSupport.Settings(A28TestSupport.Password().PadRight(64, 'x')));
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var me = await http.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal("authentication_required", (await Json(me)).GetProperty("code").GetString());
        Assert.DoesNotContain(token, await me.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("wrapped-read")]
    [InlineData("save")]
    public async Task Login_database_read_or_write_failure_returns_503_without_token_or_SQL_details(string failure)
    {
        var fault = new A28TestSupport.DataFault();
        using var factory = A28TestSupport.Factory(fault: fault); using var http = factory.CreateClient();
        var password = A28TestSupport.Password(); var user = await A28TestSupport.Seed(factory, password);
        if (failure == "save") fault.FailSave = true; else { fault.FailRead = true; fault.WrapReadFailure = failure == "wrapped-read"; }
        using var result = await Login(http, user.Email, password);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        var body = await result.Content.ReadAsStringAsync();
        Assert.Contains("auth_unavailable", body); Assert.DoesNotContain("SYNTHETIC_PRIVATE_SQL_ERROR", body); Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UnavailableStore(bool wrapped) : ISessionStateStore
    {
        public Task<UserSessionState?> GetAsync(int userId, CancellationToken ct) =>
            throw (wrapped ? new InvalidOperationException("Synthetic provider wrapper", new A28TestSupport.SyntheticDatabaseException()) : new A28TestSupport.SyntheticDatabaseException());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_database_failure_fails_closed_but_does_not_break_anonymous_health(bool wrapped)
    {
        using var factory = A28TestSupport.Factory().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        { s.RemoveAll<ISessionStateStore>(); s.AddScoped<ISessionStateStore>(_ => new UnavailableStore(wrapped)); }));
        using var http = factory.CreateClient(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        http.DefaultRequestHeaders.Authorization = new("Bearer", A28TestSupport.Token(user));
        using var me = await http.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.ServiceUnavailable, me.StatusCode);
        using var health = await http.GetAsync("/api/health"); await TestSupport.AssertHealthResponseAsync(health);
    }

    [Fact]
    public async Task Login_limit_is_five_per_IP_and_does_not_replace_upload_rejections()
    {
        using var factory = A28TestSupport.Factory(new() { ["Auth:LoginAttemptsPerMinute"] = "5", ["CvUpload:Endpoint:PermitLimit"] = "1" });
        using var http = factory.CreateClient();
        for (var i = 0; i < 5; i++) using (var response = await Login(http, "absent@example.invalid", "synthetic9")) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        http.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.10");
        using var limited = await Login(http, "absent@example.invalid", "synthetic9");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter); Assert.Equal("login_rate_limited", (await Json(limited)).GetProperty("code").GetString());
        var uploadUser = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        http.DefaultRequestHeaders.Authorization = new("Bearer", A28TestSupport.Token(uploadUser));
        using var firstUpload = await http.PostAsync("/api/cvs/upload", new MultipartFormDataContent());
        using var secondUpload = await http.PostAsync("/api/cvs/upload", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.TooManyRequests, secondUpload.StatusCode);
        Assert.Contains("tải CV", await secondUpload.Content.ReadAsStringAsync());
        await TestSupport.AssertHealthResponseAsync(await http.GetAsync("/api/health"));
    }

    [Fact]
    public async Task Successful_and_failed_login_do_not_log_password_token_or_key()
    {
        var logs = new A28TestSupport.LogCapture(); using var factory = A28TestSupport.Factory(logs: logs); using var http = factory.CreateClient();
        var password = A28TestSupport.Password(); var user = await A28TestSupport.Seed(factory, password);
        using var success = await Login(http, user.Email, password);
        var token = (await success.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        using var failed = await Login(http, "absent@example.invalid", password);
        var text = string.Join('\n', logs.Messages);
        foreach (var secret in new[] { password, token, A28TestSupport.Key, user.PasswordHash }) Assert.DoesNotContain(secret, text);
    }

    [Theory]
    [InlineData("Jwt:Key", null)]
    [InlineData("Jwt:Key", "short-synthetic")]
    [InlineData("Jwt:Issuer", "")]
    [InlineData("Jwt:Audience", "")]
    [InlineData("Jwt:ExpiresHours", "0")]
    [InlineData("Auth:LoginAttemptsPerMinute", "0")]
    [InlineData("Auth:LoginAttemptsPerMinute", "101")]
    public void Invalid_configuration_stops_startup_with_safe_options_error(string key, string? value)
    {
        using var factory = A28TestSupport.Factory(new() { [key] = value });
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(key.Split(':')[0], error.Message);
        if (key == "Jwt:Key" && value is not null) Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public async Task Privacy_and_business_routes_require_a_real_HR_token_after_A29()
    {
        using var factory = A28TestSupport.Factory(); using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/retention/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/data-requests")).StatusCode);
        using var hr = await TestSupport.ClientWithSyntheticHrAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await hr.GetAsync("/api/retention/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hr.GetAsync("/api/data-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hr.GetAsync("/api/jobs")).StatusCode);
    }

    [Fact]
    public void A29_and_A30_protect_business_and_account_routes()
    {
        using var factory = A28TestSupport.Factory();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var protectedRoutes = routes.Where(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0).ToList();
        Assert.Contains(protectedRoutes, e => e.RoutePattern.RawText == "/api/auth/me");
        Assert.Contains(protectedRoutes, e => e.RoutePattern.RawText?.TrimEnd('/') == "/api/jobs");
        Assert.Contains(routes, e => e.RoutePattern.RawText == "/api/auth/change-password" && e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == AuthPolicies.SignedIn));
        Assert.NotNull(factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy);
    }
}
