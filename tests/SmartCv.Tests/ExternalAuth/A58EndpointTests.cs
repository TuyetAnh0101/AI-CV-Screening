using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Api.Auth;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.ExternalAuth;

public sealed class A58EndpointTests
{
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
