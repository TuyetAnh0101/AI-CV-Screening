using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Api.Auth;

namespace SmartCv.Tests.Auth;

/// <summary>
/// A route added in a later branch must declare a recognized security boundary.
/// Business routes inherit BusinessOperator from the API group, but public and
/// SignedIn routes intentionally use a different boundary.
/// </summary>
public sealed class RoleBoundaryRoutePolicyTests
{
    [Fact]
    public void Every_protected_api_endpoint_has_an_explicit_boundary()
    {
        using var factory = A28TestSupport.Factory();
        using var http = factory.CreateClient();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty).StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(routes);
        foreach (var endpoint in routes)
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                continue;

            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(x => x.Policy).Where(x => x is not null).ToArray();
            var protectedByNamedPolicy = policies.Any(policy =>
                policy is AuthPolicies.BusinessOperator or AuthPolicies.WorkspaceOwner
                    or AuthPolicies.SystemAdmin or AuthPolicies.Admin or AuthPolicies.SignedIn);

            Assert.True(protectedByNamedPolicy,
                $"Route /{endpoint.RoutePattern.RawText} lacks explicit boundary. " +
                $"Got: {string.Join(",", policies)}");
        }
    }
}
