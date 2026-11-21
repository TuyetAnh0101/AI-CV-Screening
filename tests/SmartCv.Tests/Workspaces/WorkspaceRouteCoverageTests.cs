using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Tests.Auth;
using Xunit.Abstractions;

namespace SmartCv.Tests;

public class WorkspaceRouteCoverageTests(ITestOutputHelper output)
{
    internal static readonly Dictionary<string, string> Exceptions = new()
    {
        ["GET /api/auth/me"] = "SignedIn: current authenticated account only; A52SessionTests.",
        ["POST /api/auth/change-password"] = "SignedIn: current account and current password; A30SecurityTests.",
        ["POST /api/auth/login"] = "Anonymous: issues a token after credentials and membership validation; A28AuthEndpointTests.",
        ["GET /api/health"] = "Anonymous: public readiness contract; no recruitment records.",
        ["GET /openapi/{documentName}.json"] = "Anonymous: Development API schema only; absent in Production."
    };

    [Fact]
    public void Every_runtime_route_has_an_explicit_security_test()
    {
        using var factory = A28TestSupport.Factory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddDataProtection().UseEphemeralDataProtectionProvider()));
        using var client = factory.CreateClient();
        var tested = WorkspaceIsolationRouteTests.ForeignUrlRoutes
            .Concat(WorkspaceIsolationRouteTests.ListRoutes.Select(r => "GET " + r))
            .Concat(new[] { "POST /api/runs", "POST /api/jobs", "POST /api/candidates", "POST /api/cvs/upload", "GET /api/dashboard/summary" })
            .Concat(WorkspaceIsolationAdditionalTests.Covered)
            .ToHashSet(StringComparer.Ordinal);
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => m + " /" + e.RoutePattern.RawText!.Trim('/'))).ToHashSet(StringComparer.Ordinal);
        var classified = tested.Concat(A29AuthorizationTests.AdminRoutes).Concat(Exceptions.Keys).ToHashSet(StringComparer.Ordinal);
        var missing = routes.Except(classified).Order().ToArray();
        foreach (var route in missing) output.WriteLine("MISSING ISOLATION TEST: " + route);
        Assert.True(missing.Length == 0, $"{missing.Length} route(s) lack explicit isolation tests: " + string.Join("; ", missing));
        Assert.Empty(classified.Except(routes));
    }

    public static IEnumerable<object[]> AdminRoutes() => A29AuthorizationTests.AdminRoutes.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Workspace_owner_with_HR_role_cannot_call_system_admin_routes(string route)
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var client = fixture.Client(2);
        var parts = route.Split(' ', 2);
        var path = parts[1].Replace("{id:int}", fixture.HrA.UserId.ToString())
            .Replace("{id:long}", "1").Replace("{id:guid}", fixture.Data[1].Cv.ToString());
        using var response = await client.SendAsync(new(new(parts[0]), path));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("access_denied", body);
        Assert.DoesNotContain("A52", body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Runtime_inventory_includes_each_method_and_conditional_endpoint(bool enabled)
    {
        using var factory = A28TestSupport.Factory(new() { ["CvUpload:Endpoint:Enabled"] = enabled.ToString() }).WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddDataProtection().UseEphemeralDataProtectionProvider()));
        using var client = factory.CreateClient();
        var rows = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (Endpoint: e, Method: method, Path: "/" + e.RoutePattern.RawText!.Trim('/'))))
            .OrderBy(r => r.Path).ThenBy(r => r.Method).ToList();
        foreach (var row in rows)
        {
            var metadata = row.Endpoint.Metadata;
            var policies = string.Join("+", metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "Business").Distinct());
            var category = metadata.GetMetadata<IAllowAnonymous>() is not null ? "Anonymous"
                : policies.Contains("Admin") ? "System Admin" : policies.Contains("SignedIn") ? "SignedIn" : "Business";
            var handler = metadata.GetMetadata<MethodInfo>();
            output.WriteLine($"A53ROUTE|{enabled}|{row.Method}|{row.Path}|{metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? "-"}|{category}|{policies}|{handler?.DeclaringType?.FullName}.{handler?.Name}");
        }
        output.WriteLine($"A53COUNT|{enabled}|{rows.Count}|API={rows.Count(r => r.Path.StartsWith("/api/"))}");
        Assert.NotEmpty(rows);
        Assert.Equal(rows.Count, rows.Select(r => (r.Method, r.Path)).Distinct().Count());
    }
}
