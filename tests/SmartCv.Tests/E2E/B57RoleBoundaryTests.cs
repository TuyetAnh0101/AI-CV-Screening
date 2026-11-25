using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Api.Auth;

namespace SmartCv.Tests.E2E;

/// <summary>
/// B57 R1/R2/R4/R8: route matrix taken from the RUNNING endpoint table (not a hand-written list), so a new route
/// cannot silently escape the Admin/HR/Owner/Anonymous boundaries. Every denial is checked for its status, its
/// machine code and for the absence of any workspace marker.
/// </summary>
public sealed class B57RoleBoundaryTests
{
    internal sealed record RouteRow(string Method, string Pattern, string[] Policies, bool Anonymous)
    {
        internal bool SystemAdmin => Policies.Any(p => p is AuthPolicies.SystemAdmin or AuthPolicies.Admin);
        internal bool Business => !Anonymous && !SystemAdmin && !Policies.Contains(AuthPolicies.SignedIn);
        public override string ToString() => Method + " " + Pattern;
    }

    internal static List<RouteRow> Routes(B57World world) =>
        world.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => new RouteRow(m,
                "/" + e.RoutePattern.RawText!.Trim('/'),
                [.. e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "Default").Distinct()],
                e.Metadata.GetMetadata<IAllowAnonymous>() is not null)))
            .Where(r => r.Pattern.StartsWith("/api/", StringComparison.Ordinal))
            .ToList();

    internal static HttpRequestMessage Request(B57World world, RouteRow route, int foreignWorkspace = B57World.OwnerBWorkspace)
    {
        var g = world.Data[foreignWorkspace];
        var path = System.Text.RegularExpressions.Regex.Replace(route.Pattern, "\\{([^}]+)\\}", m => m.Groups[1].Value switch
        {
            var v when v.Contains(":guid") => g.Cvs[0].ToString(),
            var v when v.StartsWith("flagId") => g.Flag.ToString(),
            var v when v.StartsWith("applicationId") => g.Applications[0].ToString(),
            var v when v.StartsWith("runId") => g.Run.ToString(),
            "code" => "b57s3",
            "provider" => "google",
            "documentName" => "v1",
            var v when v.Contains(":long") => "1",
            _ => route.Pattern.Contains("/jobs/") ? g.Job.ToString() : route.Pattern.Contains("/candidates/") ? g.Candidates[0].ToString()
                : route.Pattern.Contains("/users/") ? world.OwnerB.UserId.ToString() : route.Pattern.Contains("/runs/") ? g.Run.ToString()
                : route.Pattern.Contains("/prompts/") ? g.Prompt.ToString() : route.Pattern.Contains("/evaluation-sets/") ? g.Set.ToString()
                : route.Pattern.Contains("/evaluations/") ? g.Evaluations[0].ToString() : route.Pattern.Contains("/workspaces/") ? foreignWorkspace.ToString()
                : g.Applications[0].ToString()
        });
        return new HttpRequestMessage(new HttpMethod(route.Method), path)
        {
            Content = route.Method is "GET" or "DELETE" or "HEAD" ? null : new StringContent("{}", Encoding.UTF8, "application/json")
        };
    }

    private static async Task AssertDenied(HttpResponseMessage response, HttpStatusCode status, string code, string route)
    {
        Assert.True(status == response.StatusCode, $"{route}: expected {(int)status}, got {(int)response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        foreach (var w in B57World.Workspaces) Assert.DoesNotContain(B57World.Marker(w), body);
        Assert.DoesNotContain("@example.invalid", body);
    }

    [Fact]
    public async Task R1_Admin_without_business_role_is_403_on_every_business_and_owner_route_and_changes_nothing()
    {
        using var world = await B57World.CreateAsync();
        using var admin = world.AdminClient();
        var business = Routes(world).Where(r => r.Business).ToList();
        Assert.Contains(business, r => r.Pattern == "/api/hr/reports/export");
        Assert.Contains(business, r => r.Pattern == "/api/runs/{id:int}/ranking/export");
        Assert.Contains(business, r => r.Pattern == "/api/cvs/{id:guid}/file");
        Assert.True(business.Count > 80, "Business route inventory unexpectedly small: " + business.Count);
        foreach (var route in business)
        {
            using var request = Request(world, route);
            using var response = await admin.SendAsync(request);
            await AssertDenied(response, HttpStatusCode.Forbidden, "access_denied", route.ToString());
        }
        await world.WithDb(B57World.OwnerBWorkspace, async db =>
        {
            Assert.Single(db.EvaluationRuns);
            Assert.Equal(2, db.Applications.Count());
            Assert.Equal(SmartCv.Core.Constants.ApplicationStages.Shortlisted, db.Applications.Single(a => a.ApplicationId == world.Data[3].Applications[0]).Stage);
        });
        await world.WithGlobalDb(async db => Assert.DoesNotContain(db.AuditEvents, e => e.Outcome == "AccessGranted" && e.ActionCode.Contains("export")));
    }

    [Theory]
    [InlineData(B57World.Internal)]
    [InlineData(B57World.OwnerAWorkspace)]
    public async Task R2_R4_HR_and_Owner_are_403_on_every_system_admin_route_including_both_admin_report_versions(int workspace)
    {
        using var world = await B57World.CreateAsync();
        using var hr = world.Business(workspace);
        var admin = Routes(world).Where(r => r.SystemAdmin).ToList();
        foreach (var required in new[] { "/api/users", "/api/users/{id:int}/lock", "/api/users/{id:int}/reset-password", "/api/admin/audit-events",
                     "/api/admin/reports/summary", "/api/admin/reports/download", "/api/admin/reports/v2/preview", "/api/admin/reports/v2/download",
                     "/api/admin/ops/overview", "/api/admin/email/health", "/api/exports" })
            Assert.Contains(admin, r => r.Pattern == required);
        foreach (var route in admin)
        {
            using var request = Request(world, route);
            using var response = await hr.SendAsync(request);
            await AssertDenied(response, HttpStatusCode.Forbidden, "access_denied", route.ToString());
        }
        await world.WithGlobalDb(async db =>
        {
            Assert.True(db.Users.Single(u => u.UserId == world.OwnerB.UserId).IsActive);
            Assert.DoesNotContain(db.AuditEvents, e => e.ActionCode == "admin.operations_report_exported" && e.Outcome == "AccessGranted");
        });
    }

    [Fact]
    public async Task R8_anonymous_is_401_on_every_protected_api_route_including_reports_and_files()
    {
        using var world = await B57World.CreateAsync();
        using var anonymous = world.Anonymous();
        var routes = Routes(world).Where(r => !r.Anonymous).ToList();
        foreach (var required in new[] { "/api/hr/reports/summary", "/api/hr/reports/export/pdf", "/api/admin/reports/v2/download", "/api/cvs/{id:guid}/file", "/api/runs/{id:int}/ranking/export" })
            Assert.Contains(routes, r => r.Pattern == required);
        foreach (var route in routes)
        {
            using var request = Request(world, route);
            using var response = await anonymous.SendAsync(request);
            await AssertDenied(response, HttpStatusCode.Unauthorized, "authentication_required", route.ToString());
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        }
    }

    [Fact]
    public async Task R7_Admin_is_allowed_on_admin_read_routes_and_never_receives_business_markers()
    {
        using var world = await B57World.CreateAsync();
        using var admin = world.AdminClient();
        var sqlOnly = new List<string>();
        foreach (var route in Routes(world).Where(r => r.SystemAdmin && r.Method == "GET"))
        {
            using var request = Request(world, route);
            using var response = await admin.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            // Raw-SQL Admin Ops readers refuse EF InMemory explicitly; they are exercised by B57SqlServerTests.
            if (response.StatusCode == HttpStatusCode.InternalServerError && ((body.Contains("NotSupportedException") && body.Contains("cần SQL Server"))
                || body.Contains("Relational-specific methods can only be used")))
            { sqlOnly.Add(route.Pattern); continue; }
            Assert.True(response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.InternalServerError),
                $"{route}: Admin got {(int)response.StatusCode} {body[..Math.Min(body.Length, 700)]}");
            // Account/workspace administration may show synthetic account e-mails; never CV, candidate, prompt or score content.
            foreach (var w in B57World.Workspaces)
            {
                var marker = B57World.Marker(w);
                foreach (var secret in new[] { " CV TEXT SECRET", " candidate ", " SYSTEM PROMPT SECRET", " SNAPSHOT SECRET", " REASONING SECRET", " RAW SECRET", "-cv-0.pdf" })
                    Assert.DoesNotContain(marker + secret, body);
            }
            Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("securityStamp", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AQAAAA", body); // ASP.NET Identity v3 password-hash prefix
        }
        Assert.Subset(B57SqlServerTests.SqlOnlyAdminRoutes.ToHashSet(), sqlOnly.ToHashSet());
    }
}
