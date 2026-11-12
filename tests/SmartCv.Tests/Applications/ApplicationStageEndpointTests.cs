using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Applications;

public class ApplicationStageEndpointTests
{
    [Fact]
    public async Task Options_have_six_codes_and_vietnamese_labels()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var options = (await client.GetFromJsonAsync<StageOptionsResponse>("/api/applications/stage-options"))!;
        Assert.Equal(ApplicationStages.All, options.Stages.Select(s => s.Code));
        Assert.Equal(RejectReasons.All, options.RejectReasons.Select(r => r.Code));
        Assert.All(options.Stages, s => Assert.Equal(ApplicationStages.DisplayName(s.Code), s.DisplayName));
        Assert.All(options.RejectReasons, r => Assert.Equal(RejectReasons.DisplayName(r.Code), r.DisplayName));
    }

    public static IEnumerable<object[]> Transitions =>
        from source in ApplicationStages.All from target in ApplicationStages.All select new object[] { source, target };

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task Every_transition_is_allowed_and_noop_has_no_history(string from, string to)
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" });
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services, 1);
        await WithDb(factory.Services, async db =>
        {
            var app = await db.Applications.SingleAsync();
            app.Stage = from;
            app.RejectReason = from == "Rejected" ? "Duplicate" : null;
            await db.SaveChangesAsync();
        });
        var request = Request(to, to == "Rejected" ? "Duplicate" : null);
        using var response = await client.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<StageChangeResultDto>())!;
        Assert.Equal(from == to ? "Unchanged" : "Changed", result.Outcome);
        Assert.Equal(to, result.Stage);
        Assert.Equal(from, result.FromStage);
        await WithDb(factory.Services, async db =>
        {
            Assert.Equal(from == to ? 0 : 1, await db.ApplicationEvents.CountAsync());
            Assert.Equal(to, (await db.Applications.SingleAsync()).Stage);
        });
        if (from != to)
        {
            Assert.NotNull(result.EventId);
            Assert.Equal(DateTimeKind.Utc, result.StageUpdatedAt!.Value.Kind);
        }
        else Assert.Null(result.EventId);
    }

    [Fact]
    public async Task Rejection_reason_change_is_a_decision_but_note_or_actor_only_is_unchanged()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = (await SeedAsync(factory.Services, 1))[0];
        using var first = await client.PatchAsJsonAsync($"/api/applications/{id}/stage", Request("Rejected", "MissingSkills"));
        var firstResult = (await first.Content.ReadFromJsonAsync<StageChangeResultDto>())!;
        var reasonChange = Request("Rejected", "NotEnoughExperience");
        reasonChange.ExpectedStageVersion = firstResult.StageVersion;
        using var second = await client.PatchAsJsonAsync($"/api/applications/{id}/stage", reasonChange);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondResult = (await second.Content.ReadFromJsonAsync<StageChangeResultDto>())!;
        Assert.Equal("Changed", secondResult.Outcome);
        Assert.NotEqual(firstResult.StageVersion, secondResult.StageVersion);
        Assert.NotEqual(firstResult.EventId, secondResult.EventId);
        reasonChange.Note = "Changed note only";
        reasonChange.Actor = "HR 2";
        reasonChange.ExpectedStageVersion = secondResult.StageVersion;
        using var noop = await client.PatchAsJsonAsync($"/api/applications/{id}/stage", reasonChange);
        var unchanged = (await noop.Content.ReadFromJsonAsync<StageChangeResultDto>())!;
        Assert.Equal("Unchanged", unchanged.Outcome);
        Assert.Null(unchanged.EventId);
        Assert.Equal(secondResult.StageUpdatedAt, unchanged.StageUpdatedAt);
        Assert.Equal(secondResult.StageVersion, unchanged.StageVersion);
        var history = (await client.GetFromJsonAsync<List<ApplicationEventDto>>($"/api/applications/{id}/events"))!;
        Assert.Equal(2, history.Count);
        Assert.Equal(new[] { "NotEnoughExperience", "MissingSkills" }, history.Select(e => e.RejectReason));
        Assert.All(history, e => { Assert.Equal("HR - Trần Thu Hà", e.Actor); Assert.Equal("Đủ kỹ năng", e.Note); });
        Assert.All(history, e => Assert.Equal(DateTimeKind.Utc, e.CreatedAt.Kind));
    }

    public static IEnumerable<object?[]> InvalidChanges => new object?[][]
    {
        [null, null, "HR", null], ["", null, "HR", null], ["Hired", null, "HR", null],
        ["reviewing", null, "HR", null], ["Rejected", null, "HR", null],
        ["Rejected", "invalid", "HR", null], ["Interview", "Duplicate", "HR", null],
        ["Reviewing", null, null, null], ["Reviewing", null, "  ", null],
        ["Reviewing", null, new string('a', 101), null], ["Reviewing", null, "HR", new string('a', 501)]
    };

    [Theory]
    [MemberData(nameof(InvalidChanges))]
    public async Task Invalid_single_and_bulk_requests_return_400_without_writes(string? stage, string? reason, string? actor, string? note)
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = (await SeedAsync(factory.Services, 1))[0];
        var body = new { toStage = stage, rejectReason = reason, actor, note };
        using var single = await client.PatchAsJsonAsync($"/api/applications/{id}/stage", body);
        using var bulk = await client.PostAsJsonAsync("/api/applications/stage",
            new { applicationIds = new[] { id }, toStage = stage, rejectReason = reason, actor, note });
        Assert.Equal(HttpStatusCode.BadRequest, single.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bulk.StatusCode);
        await WithDb(factory.Services, async db =>
        {
            Assert.Empty(await db.ApplicationEvents.ToListAsync());
            Assert.Equal("New", (await db.Applications.SingleAsync()).Stage);
        });
    }

    [Fact]
    public async Task Missing_invalid_json_and_invalid_expectations_have_correct_status()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = (await SeedAsync(factory.Services, 1))[0];
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync("/api/applications/999999/stage", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/applications/999999/events")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<ApplicationEventDto>>($"/api/applications/{id}/events"))!);
        using var invalid = await client.PatchAsync($"/api/applications/{id}/stage", new StringContent("{broken", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var request = Request();
        request.ExpectedStage = "INVALID";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/applications/{id}/stage", request)).StatusCode);
        request.ExpectedStage = "New";
        request.ExpectedStageVersion = Guid.Empty;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/applications/{id}/stage", request)).StatusCode);
        request.ExpectedStageVersion = null;
        request.ExpectedStage = "Rejected";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"/api/applications/{id}/stage", request)).StatusCode);
    }

    [Fact]
    public async Task Stale_version_detects_reason_change_and_return_to_same_stage()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = (await SeedAsync(factory.Services, 1))[0];
        var original = Guid.Empty;
        await WithDb(factory.Services, async db => original = (await db.Applications.SingleAsync()).StageVersion);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/applications/{id}/stage", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/applications/{id}/stage", Request("New"))).StatusCode);
        var request = Request("Interview");
        request.ExpectedStage = "New";
        request.ExpectedStageVersion = original;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"/api/applications/{id}/stage", request)).StatusCode);
        await WithDb(factory.Services, async db => Assert.Equal(2, await db.ApplicationEvents.CountAsync()));
    }

    [Fact]
    public async Task Bulk_preserves_order_and_reports_changed_unchanged_missing_and_repeated_request()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services, 3);
        await client.PatchAsJsonAsync($"/api/applications/{ids[1]}/stage", Request());
        var request = new BulkChangeStageRequest { ApplicationIds = [ids[2], 999999, ids[0], ids[1]], ToStage = "Reviewing", Actor = "HR" };
        using var response = await client.PostAsJsonAsync("/api/applications/stage", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bulk = (await response.Content.ReadFromJsonAsync<BulkStageChangeResponse>())!;
        Assert.Equal((4, 2, 1, 1), (bulk.Total, bulk.Changed, bulk.Unchanged, bulk.NotFound));
        Assert.Equal(request.ApplicationIds, bulk.Results.Select(r => r.ApplicationId));
        Assert.Equal(new[] { "Changed", "NotFound", "Changed", "Unchanged" }, bulk.Results.Select(r => r.Outcome));
        Assert.All(bulk.Results.Where(r => r.Outcome == "Changed"), r => Assert.NotNull(r.EventId));
        using var again = await client.PostAsJsonAsync("/api/applications/stage", request);
        var repeated = (await again.Content.ReadFromJsonAsync<BulkStageChangeResponse>())!;
        Assert.Equal((0, 3, 1), (repeated.Changed, repeated.Unchanged, repeated.NotFound));
        await WithDb(factory.Services, async db => Assert.Equal(3, await db.ApplicationEvents.CountAsync()));
    }

    [Fact]
    public async Task Bulk_accepts_200_and_rejects_201_null_duplicates_nonpositive_or_unrelated_versions()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services, 200);
        var request = new BulkChangeStageRequest { ApplicationIds = ids, ToStage = "Shortlisted", Actor = "HR" };
        using var valid = await client.PostAsJsonAsync("/api/applications/stage", request);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(200, (await valid.Content.ReadFromJsonAsync<BulkStageChangeResponse>())!.Changed);
        foreach (var invalid in new List<int>?[] { null, [], [1, 1], [0], [-1], Enumerable.Range(1, 201).ToList() })
        {
            request.ApplicationIds = invalid!;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/applications/stage", request)).StatusCode);
        }
        request.ApplicationIds = [ids[0]];
        request.ExpectedStageVersions = new() { [999999] = Guid.NewGuid() };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/applications/stage", request)).StatusCode);
        await WithDb(factory.Services, async db => Assert.Equal(200, await db.ApplicationEvents.CountAsync()));
    }

    [Fact]
    public async Task Bulk_stale_version_returns_per_item_results_and_changes_nothing()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services, 2);
        var request = new BulkChangeStageRequest
        {
            ApplicationIds = [ids[1], 999999, ids[0]], ToStage = "Interview", Actor = "HR",
            ExpectedStageVersions = new() { [ids[0]] = Guid.NewGuid() }
        };
        using var response = await client.PostAsJsonAsync("/api/applications/stage", request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("rolledBack").GetBoolean());
        Assert.Equal(new[] { "RolledBack", "NotFound", "Conflict" }, json.RootElement.GetProperty("results")
            .EnumerateArray().Select(r => r.GetProperty("outcome").GetString()));
        await WithDb(factory.Services, async db =>
        {
            Assert.Empty(await db.ApplicationEvents.ToListAsync());
            Assert.All(await db.Applications.ToListAsync(), a => Assert.Equal("New", a.Stage));
        });
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", false)]
    public async Task Stage_endpoints_preserve_existing_access_boundary(string environment, bool enabled)
    {
        using var factory = TestSupport.ApiFactory(environment, new() { ["CvUpload:Endpoint:Enabled"] = enabled.ToString() });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/applications/stage-options")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/applications/1/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync("/api/applications/1/stage", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/applications/stage", new BulkChangeStageRequest())).StatusCode);
    }

    [Fact]
    public async Task Application_with_decision_cannot_be_unassigned()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = (await SeedAsync(factory.Services, 1))[0];
        await client.PatchAsJsonAsync($"/api/applications/{id}/stage", Request());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/applications/{id}")).StatusCode);
        await WithDb(factory.Services, async db => Assert.Single(await db.ApplicationEvents.ToListAsync()));
    }

    internal static ChangeStageRequest Request(string stage = "Reviewing", string? reason = null) => new()
    { ToStage = stage, RejectReason = reason, Actor = "  HR - Trần Thu Hà  ", Note = "  Đủ kỹ năng  " };

    internal static async Task<List<int>> SeedAsync(IServiceProvider services, int count)
    {
        var ids = new List<int>();
        await WithDb(services, async db =>
        {
            await db.Database.EnsureCreatedAsync();
            var apps = Enumerable.Range(1, count).Select(_ => NewApplication()).ToList();
            db.AddRange(apps);
            await db.SaveChangesAsync();
            ids.AddRange(apps.Select(a => a.ApplicationId));
        });
        return ids;
    }

    internal static Application NewApplication() => new()
    {
        JobId = 1, CreatedAt = DateTime.UtcNow,
        Cv = new Cv
        {
            CvId = Guid.NewGuid(), OriginalFileName = "synthetic.pdf", ContentType = "application/pdf", StoredPath = "test",
            FileSizeBytes = 1,
            FileHash = Guid.NewGuid().ToString("N").PadRight(64, '0'), UploadedAt = DateTime.UtcNow,
            ExtractionStatus = "Completed"
        }
    };

    internal static async Task WithDb(IServiceProvider services, Func<SmartCvDbContext, Task> action)
    {
        using var scope = services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
    }
}
