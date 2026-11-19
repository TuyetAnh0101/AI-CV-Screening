using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartCv.Core.Contracts;

namespace SmartCv.Tests.Dashboard;

public class DashboardEndpointTests
{
    internal static WebApplicationFactory<Program> Factory(string environment = "Development", bool enabled = true) =>
        TestSupport.ApiFactory(environment, new()
        {
            ["CvUpload:Endpoint:Enabled"] = enabled.ToString(),
            ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false"
        });

    [Theory]
    [InlineData("jobId=abc")]
    [InlineData("jobId=-1")]
    [InlineData("jobId=0")]
    [InlineData("jobId=2147483648")]
    [InlineData("jobId=1.1")]
    [InlineData("jobId=1&jobId=2")]
    [InlineData("from=2026-11-18&to=2026-11-17")]
    [InlineData("from=2026-02-29")]
    [InlineData("to=9999-12-31")]
    [InlineData("from=0001-01-01")]
    [InlineData("from=2026-11-01&from=2026-11-02")]
    [InlineData("to=2026-11-17T00:00:00Z")]
    public async Task Bad_filters_return_Vietnamese_problem_without_touching_database(string query)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/dashboard/summary?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Contains("không hợp lệ", body!.Title);
        Assert.False(string.IsNullOrWhiteSpace(body.Detail));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public async Task Dashboard_is_not_mapped_outside_the_HR_gate(string environment)
    {
        using var factory = Factory(environment, false);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/dashboard/summary");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task A29_requires_authentication_in_other_environments_and_allows_authorized_HR(string environment)
    {
        using var factory = Factory(environment, true);
        using var client = await TestSupport.ClientWithSyntheticHrAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dashboard/summary")).StatusCode);
        using var secured = TestSupport.ApiFactory(environment, new() { ["CvUpload:Endpoint:Enabled"] = "true" }, authenticated: false);
        using var anonymous = secured.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/dashboard/summary")).StatusCode);
    }
}
