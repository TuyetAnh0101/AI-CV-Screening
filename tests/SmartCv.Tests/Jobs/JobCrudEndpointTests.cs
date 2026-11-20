using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SmartCv.Core.Contracts;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Jobs;

public class JobCrudEndpointTests
{
    internal static WebApplicationFactory<Program> Factory(string environment = "Development", bool enabled = true, JobClock? clock = null) =>
        TestSupport.ApiFactory(environment, new()
        {
            ["CvUpload:Endpoint:Enabled"] = enabled.ToString(), ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false"
        }).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(clock ?? new JobClock());
            foreach (var descriptor in s.Where(d => d.ImplementationType is { } type && typeof(BackgroundService).IsAssignableFrom(type)).ToArray()) s.Remove(descriptor);
        }));

    internal static async Task SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task Create_update_and_idempotent_toggle_preserve_old_contract_and_return_field_validation()
    {
        using var factory = Factory(); await SeedAsync(factory); using var client = factory.CreateClient();
        var request = JobRulesTests.Valid("  Kỹ sư\tBackend  "); request.MinGpa = 3.456m; request.MinYearsExperience = 1.24m;
        using var created = await client.PostAsJsonAsync("/api/jobs", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType!.MediaType);
        Assert.True(created.Headers.CacheControl!.NoStore);
        var job = (await created.Content.ReadFromJsonAsync<JobDto>())!;
        Assert.Equal($"/api/jobs/{job.JobId}", created.Headers.Location!.OriginalString);
        Assert.Equal("Kỹ sư Backend", job.Title); Assert.Equal(3.46m, job.MinGpa); Assert.Equal(1.2m, job.MinYearsExperience);
        Assert.True(job.IsActive); Assert.False(job.IsExpired); Assert.False(job.HasEvaluations); Assert.Equal(0, job.ApplicationCount);
        Assert.Equal(ApplicationStages.All, job.StageCounts!.Keys); Assert.All(job.StageCounts.Values, count => Assert.Equal(0, count));
        Assert.Empty(job.Skills); Assert.Equal(DateTimeKind.Utc, job.CreatedAt.Kind);
        using var duplicate = await client.PostAsJsonAsync("/api/jobs", JobRulesTests.Valid("Kỹ sư Backend"));
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "title");
        request.Title = "Tên mới A21"; request.ClosingDate = new DateOnly(2020, 1, 1); request.Headcount = null;
        using var updated = await client.PutAsJsonAsync($"/api/jobs/{job.JobId}", request);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var edit = (await updated.Content.ReadFromJsonAsync<JobDto>())!;
        Assert.Equal(request.Title, edit.Title); Assert.True(edit.IsExpired); Assert.Null(edit.Headcount); Assert.Equal(job.CreatedAt, edit.CreatedAt);
        for (var i = 0; i < 2; i++)
        {
            using var deactivated = await client.PostAsync($"/api/jobs/{job.JobId}/deactivate", null);
            Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode); Assert.False((await deactivated.Content.ReadFromJsonAsync<JobDto>())!.IsActive);
        }
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<JobDto>>("/api/jobs"))!, j => j.JobId == job.JobId);
        var all = (await client.GetFromJsonAsync<List<JobDto>>("/api/jobs?activeOnly=false"))!;
        Assert.Equal(job.JobId, all[^1].JobId); Assert.Equal(all.OrderByDescending(j => j.IsActive).ThenBy(j => j.JobId).Select(j => j.JobId), all.Select(j => j.JobId));
        Assert.False((await client.GetFromJsonAsync<JobDto>($"/api/jobs/{job.JobId}"))!.IsActive);
        for (var i = 0; i < 2; i++)
        {
            using var activated = await client.PostAsync($"/api/jobs/{job.JobId}/activate", null);
            Assert.True((await activated.Content.ReadFromJsonAsync<JobDto>())!.IsActive);
        }
        using var deletion = await client.DeleteAsync($"/api/jobs/{job.JobId}"); Assert.Equal(HttpStatusCode.MethodNotAllowed, deletion.StatusCode);
        using var missing = await client.PutAsJsonAsync("/api/jobs/2147483647", request); await AssertProblemAsync(missing, HttpStatusCode.NotFound);
        using var missingToggle = await client.PostAsync("/api/jobs/2147483647/activate", null); await AssertProblemAsync(missingToggle, HttpStatusCode.NotFound);
        request.Title = ""; using var invalid = await client.PutAsJsonAsync($"/api/jobs/{job.JobId}", request); await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "title");
    }

    [Fact]
    public async Task HR_stats_have_six_stages_and_old_skill_aliases_survive_edit_and_deactivate()
    {
        using var factory = Factory(); await SeedAsync(factory); using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var prompt = new PromptVersion { VersionName = "A21 test", SystemPrompt = "synthetic" };
            var run = new EvaluationRun { PromptVersion = prompt, RunType = RunTypes.Screening, Status = RunStatuses.Completed, KeywordWeight = .3m, AiWeight = .7m, ModelName = "synthetic", CreatedAt = DateTime.UtcNow };
            foreach (var stage in ApplicationStages.All)
            {
                var app = new Application { JobId = 1, Cv = Cv(), Stage = stage, RejectReason = stage == ApplicationStages.Rejected ? RejectReasons.Duplicate : null };
                db.Add(app);
                if (stage == ApplicationStages.New) db.Add(new Evaluation { Run = run, Application = app, Status = EvaluationStatuses.Completed });
            }
            await db.SaveChangesAsync();
        }
        var before = (await client.GetFromJsonAsync<JobDto>("/api/jobs/1"))!;
        Assert.Equal(6, before.ApplicationCount); Assert.True(before.HasEvaluations); Assert.Equal(3, before.MustHaveCount);
        Assert.All(before.StageCounts!.Values, c => Assert.Equal(1, c));
        using var update = await client.PutAsJsonAsync("/api/jobs/1", JobRulesTests.Valid("Vị trí đã đổi tên"));
        var after = (await update.Content.ReadFromJsonAsync<JobDto>())!;
        Assert.Equal(JsonSerializer.Serialize(before.Skills), JsonSerializer.Serialize(after.Skills));
        Assert.Equal(6, after.ApplicationCount); Assert.True(after.HasEvaluations);
        using var stop = await client.PostAsync("/api/jobs/1/deactivate", null);
        Assert.Equal(6, (await stop.Content.ReadFromJsonAsync<JobDto>())!.ApplicationCount);
    }

    [Theory]
    [InlineData("null", "body")]
    [InlineData("", "body")]
    [InlineData("{", "body")]
    [InlineData("[]", "body")]
    [InlineData("{\"title\":\"A\",\"minGpa\":\"NaN\"}", "minGpa")]
    [InlineData("{\"title\":\"A\",\"minYearsExperience\":1e999}", "minYearsExperience")]
    [InlineData("{\"title\":\"A\",\"headcount\":2147483648}", "headcount")]
    [InlineData("{\"title\":\"A\",\"headcount\":1.5}", "headcount")]
    [InlineData("{\"title\":\"A\",\"closingDate\":\"2026-02-29\"}", "closingDate")]
    [InlineData("{\"title\":\"A\",\"closingDate\":\"2026-11-17T00:00:00Z\"}", "closingDate")]
    [InlineData("{\"title\":\"A\",\"skillz\":[]}", "body")]
    [InlineData("{\"title\":\"A\",\"isActive\":false}", "body")]
    public async Task Malformed_null_overflow_dates_and_unknown_payload_have_Vietnamese_400(string json, string field)
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/jobs", content);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, field);
    }

    [Fact]
    public async Task No_body_wrong_content_type_oversized_body_and_request_cancellation_are_handled()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        using var noBody = await client.PostAsync("/api/jobs", null); await AssertProblemAsync(noBody, HttpStatusCode.BadRequest, "body");
        using var text = await client.PostAsync("/api/jobs", new StringContent("title=test")); await AssertProblemAsync(text, HttpStatusCode.UnsupportedMediaType);
        using var oversized = await client.PostAsync("/api/jobs", new StringContent(new string('a', 262145), Encoding.UTF8, "application/json"));
        await AssertProblemAsync(oversized, HttpStatusCode.RequestEntityTooLarge);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/api/jobs", cancellation.Token));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Closed_gate_preserves_public_GET_health_and_hides_internal_stats_and_write_routes(string environment)
    {
        using var factory = Factory(environment, false); await SeedAsync(factory); using var client = factory.CreateClient();
        using var health = await client.GetAsync("/api/health"); await TestSupport.AssertHealthResponseAsync(health);
        using var jobs = await client.GetAsync("/api/jobs"); Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
        var raw = await jobs.Content.ReadAsStringAsync();
        Assert.DoesNotContain("applicationCount", raw); Assert.DoesNotContain("hasEvaluations", raw); Assert.DoesNotContain("stageCounts", raw);
        var body = (await jobs.Content.ReadFromJsonAsync<List<JobDto>>())!;
        Assert.Equal(5, body.Count); Assert.All(body, j => { Assert.NotEmpty(j.Skills); Assert.Null(j.ApplicationCount); });
        using var create = await client.PostAsJsonAsync("/api/jobs", JobRulesTests.Valid()); Assert.Equal(HttpStatusCode.MethodNotAllowed, create.StatusCode);
        using var update = await client.PutAsJsonAsync("/api/jobs/1", JobRulesTests.Valid()); Assert.Equal(HttpStatusCode.MethodNotAllowed, update.StatusCode);
        using var stop = await client.PostAsync("/api/jobs/1/deactivate", null); Assert.Equal(HttpStatusCode.NotFound, stop.StatusCode);
        using var start = await client.PostAsync("/api/jobs/1/activate", null); Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task A29_protects_Job_writes_outside_Development(string environment)
    {
        using var factory = Factory(environment, true);
        using var client = factory.CreateClient();
        await SeedAsync(factory);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/jobs", JobRulesTests.Valid("A29 Production synthetic"))).StatusCode);
        using var secured = TestSupport.ApiFactory(environment, new() { ["CvUpload:Endpoint:Enabled"] = "true" }, authenticated: false);
        using var anonymous = secured.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/jobs", JobRulesTests.Valid())).StatusCode);
    }

    [Fact]
    public async Task Create_and_IsExpired_follow_Vietnam_midnight_not_server_day()
    {
        var clock = new JobClock(); using var factory = Factory(clock: clock); await SeedAsync(factory); using var client = factory.CreateClient();
        var request = JobRulesTests.Valid("Boundary A21");
        using var created = await client.PostAsJsonAsync("/api/jobs", request); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<JobDto>())!; Assert.False(job.IsExpired);
        clock.Now = new DateTimeOffset(2026, 11, 17, 17, 0, 0, TimeSpan.Zero);
        Assert.True((await client.GetFromJsonAsync<JobDto>($"/api/jobs/{job.JobId}"))!.IsExpired);
        request.Title = "After midnight A21";
        using var invalid = await client.PostAsJsonAsync("/api/jobs", request); await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "closingDate");
        using var edit = await client.PutAsJsonAsync($"/api/jobs/{job.JobId}", request); Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
    }

    internal static Cv Cv() => new()
    {
        CvId = Guid.NewGuid(), OriginalFileName = "synthetic-a21.pdf", StoredPath = "synthetic-a21/no-file",
        FileHash = Guid.NewGuid().ToString("N").PadRight(64,'0'), FileSizeBytes = 100, ContentType = "application/pdf",
        ExtractionStatus = ExtractionStatuses.Completed, ExtractedText = "synthetic C# SQL", UploadedAt = DateTime.UtcNow
    };

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? field = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("title").GetString()));
        if (status == HttpStatusCode.BadRequest) Assert.Equal("Dữ liệu chưa hợp lệ", json.RootElement.GetProperty("title").GetString());
        if (field is not null) Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }
}

internal sealed class JobClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 11, 17, 16, 59, 59, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
