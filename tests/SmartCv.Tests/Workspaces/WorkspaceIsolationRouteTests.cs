using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Infrastructure.Data;
using SmartCv.Core.Contracts;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests;

public class WorkspaceIsolationRouteTests
{
    [A51SqlFact]
    public async Task Sql_dashboard_timing_counts_only_current_workspace()
    {
        await using var database = await A51Database.CreateAsync(A51Database.Latest);
        await A52Data.SeedAsync(database.Options());
        using var context = database.Context();
        var connection = context.Database.GetConnectionString();
        using var factory = A28TestSupport.Factory().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<DbContextOptions<SmartCvDbContext>>();
            s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            s.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(connection));
            s.AddDataProtection().UseEphemeralDataProtectionProvider();
        }));
        foreach (var workspace in new[] { 1, 2 })
        {
            // Workspace 2 is Personal: only its verified Owner (Workspaces.OwnerUserId) is a business operator there.
            var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR", memberOfWorkspace: workspace, workspaceRole: workspace == 2 ? "Owner" : "Member");
            if (workspace == 2) await A28TestSupport.WithDb(factory, async db => { (await db.Workspaces.SingleAsync(w => w.WorkspaceId == 2)).OwnerUserId = user.UserId; await db.SaveChangesAsync(); });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", A28TestSupport.Token(user, workspaceId: workspace));
            using var response = await client.GetAsync("/api/dashboard/summary");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<DashboardSummaryDto>())!;
            Assert.Equal(1, body.TotalApplications);
            Assert.Equal(1, body.TotalCvs);
            Assert.Equal(1, body.ScreeningTime.Count);
            Assert.Single(body.RecentRuns);
            Assert.Equal($"A52 prompt {workspace}", body.RecentRuns.Single().PromptVersionName);
        }
    }
    internal static readonly string[] ListRoutes = ["/api/jobs", "/api/cvs", "/api/candidates", "/api/runs", "/api/prompts", "/api/evaluation-sets"];
    public static IEnumerable<object[]> Lists() => ListRoutes.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task Lists_contain_current_workspace_and_exclude_the_other(string route)
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        foreach (var workspace in new[] { 1, 2 })
        {
            using var client = fixture.Client(workspace);
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var marker = route == "/api/cvs" ? $"a52-{workspace}.pdf" : route == "/api/candidates" ? $"A52 candidate {workspace}"
                : route == "/api/prompts" || route == "/api/runs" ? $"A52 prompt {workspace}"
                : route == "/api/evaluation-sets" ? $"A52 set {workspace}" : $"A52 job {workspace}";
            Assert.Contains(marker, body);
            Assert.DoesNotContain(marker.Replace(workspace.ToString(), (3 - workspace).ToString()), body);
        }
    }

    [Fact]
    public async Task Run_rejects_mixed_application_ids_without_creating_a_partial_run()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var client = fixture.Client(2);
        using var response = await client.PostAsJsonAsync("/api/runs", new { applicationIds = new[] { fixture.Data[1].Application, fixture.Data[2].Application } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Hồ sơ không tồn tại", await response.Content.ReadAsStringAsync());
        await fixture.WithDb(2, async db => Assert.Equal(1, await db.EvaluationRuns.CountAsync()));
    }

    [Fact]
    public async Task Run_without_active_prompt_returns_the_workspace_setup_error_without_creating_run()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        await fixture.WithDb(2, async db => { (await db.PromptVersions.SingleAsync()).IsActive = false; await db.SaveChangesAsync(); });
        using var client = fixture.Client(2);
        using var response = await client.PostAsJsonAsync("/api/runs", new { applicationIds = new[] { fixture.Data[2].Application } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("no_active_prompt", await response.Content.ReadAsStringAsync());
        await fixture.WithDb(2, async db => Assert.Equal(1, await db.EvaluationRuns.CountAsync()));
        await fixture.WithDb(1, async db => Assert.True((await db.PromptVersions.SingleAsync()).IsActive));
    }

    [Fact]
    public async Task Admin_business_requests_cannot_read_personal_workspace()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var client = fixture.Client(1, admin: true);
        // Since #114 a System Admin is not a business operator (B57 R1): even with an internal membership the
        // business API is 403 for own-workspace lists and foreign ids alike, and never returns business data.
        foreach (var path in new[] { "/api/jobs", $"/api/jobs/{fixture.Data[1].Job}", $"/api/jobs/{fixture.Data[2].Job}", $"/api/cvs/{fixture.Data[2].Cv}", $"/api/runs/{fixture.Data[2].Run}" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("A52", await response.Content.ReadAsStringAsync());
        }
    }
    internal static readonly string[] ForeignUrlRoutes =
    [
        "GET /api/jobs/{id:int}", "POST /api/jobs/{id:int}/activate", "POST /api/jobs/{id:int}/deactivate",
        "GET /api/candidates/{id:int}", "GET /api/candidates/{id:int}/data-export",
        "GET /api/cvs/{id:guid}", "GET /api/cvs/{id:guid}/file", "GET /api/cvs/{id:guid}/text",
        "GET /api/cvs/{id:guid}/candidate-suggestion", "POST /api/cvs/{id:guid}/extract",
        "DELETE /api/applications/{id:int}", "GET /api/applications/{id:int}/events", "GET /api/applications/{id:int}/flags",
        "GET /api/applications/{id:int}/keyword-score", "POST /api/applications/{id:int}/ai-preview",
        "GET /api/evaluation-sets/{id:int}", "GET /api/evaluation-sets/{id:int}/applications",
        "GET /api/evaluation-sets/{id:int}/label-suggestions", "GET /api/evaluation-sets/{id:int}/labels/template",
        "GET /api/evaluations/{id:int}", "GET /api/prompts/{id:int}", "POST /api/prompts/{id:int}/activate",
        "GET /api/runs/{id:int}", "GET /api/runs/{id:int}/failures", "GET /api/runs/{id:int}/ranking",
        "GET /api/runs/{id:int}/ranking/export", "GET /api/runs/{id:int}/ranking/export/preview"
    ];
    public static IEnumerable<object[]> ForeignUrls() => ForeignUrlRoutes.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(ForeignUrls))]
    public async Task Foreign_id_is_indistinguishable_from_missing_and_does_not_modify_foreign_data(string route)
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var client = fixture.Client(2);
        var a = fixture.Data[1];
        var id = route.Contains("/jobs/") ? a.Job : route.Contains("/candidates/") ? a.Candidate
            : route.Contains("/applications/") ? a.Application : route.Contains("/evaluation-sets/") ? a.Set
            : route.Contains("/evaluations/") ? a.Evaluation : route.Contains("/prompts/") ? a.Prompt : a.Run;
        var parts = route.Split(' ', 2);
        var foreign = parts[1].Replace("{id:int}", id.ToString()).Replace("{id:guid}", a.Cv.ToString());
        var missing = parts[1].Replace("{id:int}", "2147483647").Replace("{id:guid}", Guid.Empty.ToString());
        using var response = await client.SendAsync(new(new(parts[0]), foreign));
        using var absent = await client.SendAsync(new(new(parts[0]), missing));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(absent.StatusCode, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var absentBody = await absent.Content.ReadAsStringAsync();
        // Existing errors can echo the supplied ID/request path, but cannot disclose database values.
        Assert.Equal(absentBody.Replace("2147483647", id.ToString()).Replace(Guid.Empty.ToString(), a.Cv.ToString()), body);
        Assert.DoesNotContain("A52 job 1", body);
        Assert.DoesNotContain("A52 candidate 1", body);
        await fixture.WithDb(1, async db =>
        {
            Assert.Equal(2, await db.Applications.CountAsync());
            Assert.Equal("New", (await db.Applications.SingleAsync(x => x.ApplicationId == a.Application)).Stage);
            Assert.True((await db.PromptVersions.SingleAsync()).IsActive);
            Assert.Null((await db.Cvs.SingleAsync(x => x.CvId == a.Cv)).AnonymizedAt);
        });
    }
}
