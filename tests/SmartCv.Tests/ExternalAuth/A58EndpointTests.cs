using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Api.Backup;
using SmartCv.Core.Contracts;
using SmartCv.Infrastructure.Backup;
using SmartCv.Api.Auth;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.ExternalAuth;

public sealed class A58EndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_Google_login_preserves_shared_and_dedicated_Drive_clients(bool dedicatedDriveClient)
    {
        using var factory = A28TestSupport.Factory(new()
        {
            ["Authentication:Google:Enabled"] = "false",
            ["Authentication:Google:ClientId"] = "synthetic-shared-client",
            ["Authentication:Google:ClientSecret"] = "synthetic-shared-secret",
            ["Backup:GoogleDrive:ClientId"] = dedicatedDriveClient ? "synthetic-drive-client" : null,
            ["Backup:GoogleDrive:ClientSecret"] = dedicatedDriveClient ? "synthetic-drive-secret" : null,
            ["Email:WorkerEnabled"] = "false"
        });
        using var scope = factory.Services.CreateScope();
        var drive = scope.ServiceProvider.GetRequiredService<GoogleDriveConnector>().Settings();
        Assert.NotNull(drive);
        Assert.Equal(dedicatedDriveClient ? "synthetic-drive-client" : "synthetic-shared-client", drive.ClientId);
        Assert.Equal(dedicatedDriveClient ? "synthetic-drive-secret" : "synthetic-shared-secret", drive.ClientSecret);
        Assert.Equal("/api/admin/backups/drive/callback", GoogleDriveConnector.CallbackPath);
        Assert.Equal("https://www.googleapis.com/auth/drive.file", GoogleDriveApi.Scope);
        using var client = factory.CreateClient();
        using var start = await client.GetAsync("/api/auth/external/google/start?intent=login");
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/external/google/complete")]
    [InlineData("/signin-google?state=forged-state&code=forged-code")]
    public async Task Forged_Google_callback_cannot_create_a_login_or_contact_Google(string callback)
    {
        using var outbound = new NoOutboundHandler();
        using var backchannel = new HttpClient(outbound);
        using var factory = A28TestSupport.Factory(new()
        {
            ["Authentication:Google:Enabled"] = "true",
            ["Authentication:Google:ClientId"] = "synthetic-client.apps.googleusercontent.com",
            ["Authentication:Google:ClientSecret"] = "synthetic-provider-test-only",
            ["App:WebBaseUrl"] = "https://app.example.invalid/",
            ["Email:WorkerEnabled"] = "false"
        }).WithWebHostBuilder(b => b.ConfigureServices(s => s.Configure<OpenIdConnectOptions>("google",
            o => o.Backchannel = backchannel)));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        using var response = await client.GetAsync(callback);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://app.example.invalid/login?external_error=provider_error", response.Headers.Location?.AbsoluteUri);
        Assert.Equal(0, outbound.Calls);
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Empty(await db.Users.ToListAsync());
            Assert.Empty(await db.Workspaces.ToListAsync());
            Assert.Empty(await db.UserExternalLogins.ToListAsync());
            Assert.Empty(await db.UserTokens.ToListAsync());
        });
    }

    private sealed class NoOutboundHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("Outbound OAuth traffic is forbidden in this test.");
        }
    }

    [Theory]
    [InlineData("github", false)]
    [InlineData("github", true)]
    [InlineData("linkedin", false)]
    [InlineData("linkedin", true)]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    public async Task Unsupported_provider_routes_reject_without_creating_identity_or_credentials(string provider, bool googleEnabled)
    {
        using var factory = A28TestSupport.Factory(new()
        {
            ["Authentication:Google:Enabled"] = googleEnabled.ToString(),
            ["Authentication:Google:ClientId"] = "synthetic-client.apps.googleusercontent.com",
            ["Authentication:Google:ClientSecret"] = "synthetic-provider-test-only",
            ["App:WebBaseUrl"] = "https://app.example.invalid/",
            ["Email:WorkerEnabled"] = "false"
        });
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", A28TestSupport.Token(user));
        using var start = await client.GetAsync($"/api/auth/external/{provider}/start?intent=login");
        using var complete = await client.GetAsync($"/api/auth/external/{provider}/complete");
        using var link = await client.PostAsJsonAsync("/api/auth/external/link-ticket", new { Provider = provider });
        using var unlink = await client.DeleteAsync($"/api/auth/me/logins/{provider}");
        foreach (var response in new[] { start, complete, unlink })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(HttpStatusCode.BadRequest, link.StatusCode);
        Assert.Contains("provider_disabled", await link.Content.ReadAsStringAsync());
        var settings = await client.GetFromJsonAsync<RegistrationSettingsResponse>("/api/auth/registration-settings");
        Assert.NotNull(settings);
        Assert.Equal(new PublicProviderSetting("google", googleEnabled), Assert.Single(settings.Providers));
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Single(await db.Users.ToListAsync());
            Assert.Single(await db.Workspaces.ToListAsync());
            Assert.Empty(await db.UserExternalLogins.ToListAsync());
            Assert.Empty(await db.UserTokens.ToListAsync());
            Assert.Empty(await db.EmailOutbox.ToListAsync());
        });
    }

    [Fact]
    public async Task GoogleDisabledDoesNotExposeAChallengeOrClaimGoogleIsEnabled()
    {
        using var factory = A28TestSupport.Factory(new() { ["Authentication:Google:Enabled"] = "false", ["Email:WorkerEnabled"] = "false" });
        using var client = factory.CreateClient();
        using var start = await client.GetAsync("/api/auth/external/google/start?intent=login");
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        using var settings = await client.GetAsync("/api/auth/registration-settings");
        var body = await settings.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        Assert.Contains("\"google\"", body);
        Assert.Contains("\"enabled\":false", body);
    }

    [Fact]
    public void AllA58EndpointsAreMappedInApiGroup()
    {
        using var factory = A28TestSupport.Factory(new() { ["Authentication:Google:Enabled"] = "false", ["Email:WorkerEnabled"] = "false" });
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.Trim('/')).ToList();
        foreach (var path in new[]
        {
            "/api/auth/external/{provider}/start", "/api/auth/external/{provider}/complete",
            "/api/auth/external/exchange", "/api/auth/external/link-ticket",
            "/api/auth/me/logins", "/api/auth/me/logins/{provider}"
        }) Assert.Contains(path, routes);
    }
}
