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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Tests.Applications;
using Xunit.Abstractions;

namespace SmartCv.Tests.Auth;

public class A29AuthorizationTests(ITestOutputHelper output)
{
    internal static readonly HashSet<string> AdminRoutes =
    [
        "POST /api/cvs/{id:guid}/anonymize", "POST /api/retention/run",
        "GET /api/health/database", "GET /api/health/gemini",
        "GET /api/admin/audit-events", "GET /api/admin/audit-events/{id:long}",
        "GET /api/exports",
        "GET /api/users", "POST /api/users", "PUT /api/users/{id:int}",
        "POST /api/users/{id:int}/lock", "POST /api/users/{id:int}/unlock", "POST /api/users/{id:int}/reset-password"
    ];

    private static string Path(RouteEndpoint endpoint) => "/" + endpoint.RoutePattern.RawText!.Trim('/');
    private static string Url(string path) => System.Text.RegularExpressions.Regex.Replace(path, "\\{([^}]+)\\}", m =>
        m.Value.Contains(":guid") ? Guid.Empty.ToString() : m.Value == "{code}" ? "SYNTHETIC" : "999999");
    private static HttpRequestMessage Request(string method, string path) => new(new HttpMethod(method), Url(path))
    { Content = method is "GET" or "DELETE" ? null : new StringContent("{}", Encoding.UTF8, "application/json") };
    internal static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, User user)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", A28TestSupport.Token(user)); return client;
    }
    private static ClaimsPrincipal Principal(User user, bool mustChange = false) => new(new ClaimsIdentity(
    [new("sub", user.UserId.ToString()), new("role", user.Role), new("smartcv_mcp", mustChange ? "true" : "false")], "Bearer", "name", "role"));

    private static Microsoft.AspNetCore.Http.HttpContext ValidatedRequest()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Items[HttpWorkspaceContext.ItemKey] = new SmartCv.Core.Workspaces.WorkspaceSessionInfo(1, "Synthetic internal", "Internal", "Member");
        return http;
    }

    [Fact]
    public async Task Regression_fixture_identity_is_created_only_in_the_current_InMemory_host()
    {
        using var factory = TestSupport.ApiFactory(); using var http = factory.CreateClient();
        using var response = await http.GetAsync("/api/applications/stage-options");
        await A28TestSupport.WithDb(factory, async db => Assert.Equal(StartupGatedApiFactory.RegressionActor, (await db.Users.SingleAsync()).FullName));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Regression_fixture_refuses_SQL_before_opening_a_connection_or_writing()
    {
        var services = new ServiceCollection();
        services.AddDbContext<SmartCv.Infrastructure.Data.SmartCvDbContext>(o => o.UseSqlServer("Server=never-connect.invalid;Database=SmartCvDb;Integrated Security=true"));
        await using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new RegressionIdentity().Token(scope.ServiceProvider));
        Assert.Contains("requires InMemory", error.Message);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("deleted")]
    [InlineData("stamp")]
    [InlineData("role")]
    [InlineData("must-change")]
    public async Task Changed_account_state_invalidates_the_next_business_request_without_a_cache_window(string change)
    {
        using var factory = A28TestSupport.Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        using var http = Client(factory, user);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/jobs")).StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var stored = await db.Users.SingleAsync();
            if (change == "locked") stored.IsActive = false;
            if (change == "deleted") db.Users.Remove(stored);
            if (change == "stamp") stored.SecurityStamp = User.NewStamp();
            if (change == "role") stored.Role = "HR";
            if (change == "must-change") stored.MustChangePassword = true;
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/jobs")).StatusCode);
    }

    [Fact]
    public async Task Endpoint_matrix_covers_every_Minimal_API_and_Controller_and_blocks_before_binding_or_side_effects()
    {
        using var factory = A28TestSupport.Factory();
        var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var anonymous = factory.CreateClient(); using var hrClient = Client(factory, hr);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => Path(e).StartsWith("/api/")).ToList();
        var auth = factory.Services.GetRequiredService<IAuthorizationService>();
        var options = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        Assert.NotNull(options.FallbackPolicy);
        Assert.Equal(95, endpoints.Count);
        var observedAdmin = new HashSet<string>();
        foreach (var endpoint in endpoints)
        {
            var path = Path(endpoint);
            var method = Assert.Single(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            var route = method + " " + path;
            var isPublic = route is "GET /api/health" or "POST /api/auth/login";
            Assert.Equal(isPublic, endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null);
            using var response = await anonymous.SendAsync(Request(method, path));
            if (isPublic)
            {
                Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                output.WriteLine($"A29MATRIX\t{method}\t{path}\tPublic\tAllowAnonymous\t-\t{(int)response.StatusCode}\tyes\tyes\tyes\t-");
                continue;
            }
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            var policy = (await AuthorizationPolicy.CombineAsync(factory.Services.GetRequiredService<IAuthorizationPolicyProvider>(), authorizeData))!;
            Assert.True((await auth.AuthorizeAsync(Principal(admin), ValidatedRequest(), policy)).Succeeded, route);
            var adminOnly = AdminRoutes.Contains(route);
            var hrAllowed = (await auth.AuthorizeAsync(Principal(hr), ValidatedRequest(), policy)).Succeeded;
            Assert.Equal(!adminOnly, hrAllowed);
            // A temporary password only reaches /api/auth/me; every business route is denied for Admin and HR alike.
            var mustChangeAllowed = (await auth.AuthorizeAsync(Principal(admin, mustChange: true), ValidatedRequest(), policy)).Succeeded;
            Assert.Equal(route is "GET /api/auth/me" or "POST /api/auth/change-password", mustChangeAllowed);
            var hrStatus = "-";
            if (adminOnly)
            {
                observedAdmin.Add(route);
                using var forbidden = await hrClient.SendAsync(Request(method, path));
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                Assert.Equal("access_denied", (await Json(forbidden)).GetProperty("code").GetString());
                hrStatus = ((int)forbidden.StatusCode).ToString();
            }
            var policies = string.Join("+", authorizeData.Select(a => a.Policy ?? "Default").Distinct());
            var roles = string.Join("/", policy.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement>()
                .Select(r => string.Join("|", r.AllowedRoles)));
            output.WriteLine($"A29MATRIX\t{method}\t{path}\tProtected\t{policies}\t{roles}\t{(int)response.StatusCode}\t{(hrAllowed ? "yes" : "no")}\tyes\t{(mustChangeAllowed ? "yes" : "no")}\t{hrStatus}");
        }
        Assert.True(AdminRoutes.SetEquals(observedAdmin));
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Empty(await db.ApplicationEvents.ToListAsync());
            Assert.Empty(await db.ApplicationFlags.ToListAsync());
            Assert.Empty(await db.CandidateDataRequests.ToListAsync());
        });
        Assert.Contains(endpoints, e => Path(e) == "/api/users");
        Assert.Contains(endpoints, e => Path(e) == "/api/auth/change-password");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("garbage")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("missing-sub")]
    [InlineData("missing-role")]
    [InlineData("duplicate-role")]
    [InlineData("invalid-id")]
    [InlineData("invalid-role")]
    [InlineData("unsigned")]
    public async Task Real_JwtBearer_rejects_invalid_tokens_on_business_routes(string kind)
    {
        using var factory = A28TestSupport.Factory(); using var http = factory.CreateClient();
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        if (kind != "missing") http.DefaultRequestHeaders.Authorization = new("Bearer", MalformedToken(user, kind));
        using var response = await http.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("authentication_required", (await Json(response)).GetProperty("code").GetString());
        await TestSupport.AssertHealthResponseAsync(await http.GetAsync("/api/health"));
    }

    private static string MalformedToken(User user, string kind)
    {
        if (kind == "garbage") return "synthetic.invalid.jwt";
        var parsed = new JsonWebToken(A28TestSupport.Token(user));
        var claims = parsed.Claims.Where(c => c.Type is not ("iss" or "aud" or "exp" or "nbf" or "iat")).ToList();
        if (kind == "missing-sub") claims.RemoveAll(c => c.Type == "sub");
        if (kind == "missing-role") claims.RemoveAll(c => c.Type == "role");
        if (kind == "duplicate-role") claims.Add(new("role", "Admin"));
        if (kind is "invalid-id" or "invalid-role")
        {
            var type = kind == "invalid-id" ? "sub" : "role";
            claims.RemoveAll(c => c.Type == type); claims.Add(new(type, kind == "invalid-id" ? "-1" : "Manager"));
        }
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims), Issuer = kind == "issuer" ? "wrong" : "SmartCv.Api",
            Audience = kind == "audience" ? "wrong" : "SmartCv.Web", IssuedAt = DateTime.UtcNow.AddHours(-10),
            NotBefore = DateTime.UtcNow.AddHours(-10), Expires = kind == "expired" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
            SigningCredentials = kind == "unsigned" ? null : new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                kind == "signature" ? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)) : A28TestSupport.Key)), SecurityAlgorithms.HmacSha256)
        });
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("deleted")]
    [InlineData("role")]
    [InlineData("stamp")]
    [InlineData("must-change")]
    public async Task Account_changes_revoke_old_privileges_immediately_even_after_a_successful_request(string change)
    {
        using var factory = A28TestSupport.Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        using var http = Client(factory, user);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/jobs")).StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var current = await db.Users.SingleAsync();
            if (change == "locked") current.IsActive = false;
            if (change == "deleted") db.Remove(current);
            if (change == "role") current.Role = "HR"; // Deliberately do not rotate the stamp.
            if (change == "stamp") current.SecurityStamp = User.NewStamp();
            if (change == "must-change") current.MustChangePassword = true;
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/jobs", new { title = "No write" })).StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR")]
    public async Task Must_change_password_allows_me_but_no_business_access(string role)
    {
        using var factory = A28TestSupport.Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role, mustChange: true);
        using var http = Client(factory, user);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/auth/me")).StatusCode);
        foreach (var route in new[] { "/api/jobs", "/api/applications/stage-options", "/api/retention/settings" })
        {
            using var response = await http.GetAsync(route); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("password_change_required", (await Json(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Single_and_bulk_decisions_use_current_database_identity_and_preserve_legacy_history()
    {
        using var factory = A28TestSupport.Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var ids = await ApplicationStageEndpointTests.SeedAsync(factory.Services, 3);
        await A28TestSupport.WithDb(factory, async db =>
        {
            db.ApplicationEvents.Add(new() { ApplicationId = ids[0], FromStage = "Reviewing", ToStage = "New", Actor = "Legacy actor", Note = "Original history" });
            var stored = await db.Users.SingleAsync(); stored.FullName = "Tên hiện tại " + new string('x', 130);
            await db.SaveChangesAsync();
        });
        using var http = Client(factory, user);
        var actor = ("Tên hiện tại " + new string('x', 130))[..100];
        using var single = await http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", new { toStage = "Reviewing", actor = "FORGED", userId = 999, role = "Admin" });
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        using var bulk = await http.PostAsJsonAsync("/api/applications/stage", new { applicationIds = ids, toStage = "Reviewing", actor = "FORGED", userId = 999 });
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
        var result = (await bulk.Content.ReadFromJsonAsync<BulkStageChangeResponse>())!;
        Assert.Equal((2, 1, 0), (result.Changed, result.Unchanged, result.NotFound));
        var history = (await http.GetFromJsonAsync<List<ApplicationEventDto>>($"/api/applications/{ids[0]}/events"))!;
        Assert.Contains(history, e => e.UserId is null && e.Actor == "Legacy actor" && e.Note == "Original history");
        Assert.Contains(history, e => e.UserId == user.UserId && e.Actor == actor);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var added = await db.ApplicationEvents.Where(e => e.UserId != null).ToListAsync();
            Assert.Equal(3, added.Count); Assert.All(added, e => { Assert.Equal(user.UserId, e.UserId); Assert.Equal(actor, e.Actor); });
        });
        Assert.DoesNotContain(user.SecurityStamp, JsonSerializer.Serialize(history));
    }

    [Fact]
    public async Task Failed_validation_noop_and_stale_version_never_add_audit_rows()
    {
        using var factory = A28TestSupport.Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var ids = await ApplicationStageEndpointTests.SeedAsync(factory.Services, 2); using var http = Client(factory, user);
        using var invalid = await http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", new { toStage = "Rejected" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var noop = await http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", new { toStage = "New" });
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        using var conflict = await http.PostAsJsonAsync("/api/applications/stage", new
        { applicationIds = ids, toStage = "Reviewing", expectedStageVersions = new Dictionary<int, Guid> { [ids[0]] = Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await A28TestSupport.WithDb(factory, async db => { Assert.Empty(await db.ApplicationEvents.ToListAsync()); Assert.All(await db.Applications.ToListAsync(), a => Assert.Equal("New", a.Stage)); });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("overlong")]
    public async Task Legacy_Actor_is_ignored_even_when_missing_blank_or_overlong(string? supplied)
    {
        using var factory = A28TestSupport.Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var ids = await ApplicationStageEndpointTests.SeedAsync(factory.Services, 2); using var http = Client(factory, user);
        var actor = supplied == "overlong" ? new string('x', 101) : supplied;
        Assert.Equal(HttpStatusCode.OK, (await http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", new { toStage = "Reviewing", actor })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/applications/stage", new { applicationIds = ids, toStage = "Reviewing", actor })).StatusCode);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var history = await db.ApplicationEvents.ToListAsync(); Assert.Equal(2, history.Count);
            Assert.All(history, e => { Assert.Equal(user.UserId, e.UserId); Assert.Equal(user.FullName, e.Actor); });
        });
    }

    [Fact]
    public async Task Database_session_failure_is_safe_503_and_public_health_still_works()
    {
        var fault = new A28TestSupport.DataFault(); using var factory = A28TestSupport.Factory(fault: fault);
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password()); using var http = Client(factory, user);
        fault.FailRead = true; fault.WrapReadFailure = true;
        using var response = await http.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_SQL_ERROR", await response.Content.ReadAsStringAsync());
        await TestSupport.AssertHealthResponseAsync(await http.GetAsync("/api/health"));
    }

    [Fact]
    public async Task Two_accounts_in_parallel_keep_separate_event_actors()
    {
        using var factory = A28TestSupport.Factory();
        var first = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var second = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var ids = await ApplicationStageEndpointTests.SeedAsync(factory.Services, 2);
        using var a = Client(factory, first); using var b = Client(factory, second);
        var responses = await Task.WhenAll(a.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", new { toStage = "Reviewing", actor = "FORGED" }),
            b.PatchAsJsonAsync($"/api/applications/{ids[1]}/stage", new { toStage = "Interview", actor = "FORGED" }));
        Assert.All(responses, r => { Assert.Equal(HttpStatusCode.OK, r.StatusCode); r.Dispose(); });
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Equal(first.UserId, (await db.ApplicationEvents.SingleAsync(e => e.ApplicationId == ids[0])).UserId);
            Assert.Equal(second.UserId, (await db.ApplicationEvents.SingleAsync(e => e.ApplicationId == ids[1])).UserId);
        });
    }

    internal static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}
