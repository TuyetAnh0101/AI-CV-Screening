extern alias web;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Privacy;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Jobs;

namespace SmartCv.Tests.Runs;

public class RunHistoryEndpointTests
{
    internal const string Erased = "ERASED_A25";
    internal const string Secret = "INTERNAL_A25_SECRET";
    internal sealed record Seed(int[] Runs, int OldPrompt, int NewPrompt, int Set, int MixedRun, int AnonymousEvaluation);
    internal static WebApplicationFactory<Program> Factory(string environment = "Development", bool enabled = true) => JobCrudEndpointTests.Factory(environment, enabled);

    internal static async Task<Seed> SeedAsync(SmartCvDbContext db, int extraRuns = 0)
    {
        var old = new PromptVersion { VersionName = "A25 old", SystemPrompt = "A25 OLD SAVED SYSTEM PROMPT", IsActive = false };
        var current = new PromptVersion { VersionName = "A25 new", SystemPrompt = "A25 NEW ACTIVE SYSTEM PROMPT", IsActive = true };
        var set = new EvaluationSet { Name = "A25 historical set", Description = "synthetic" };
        db.Add(current);
        var start = new DateTime(2026, 11, 16, 17, 0, 0, DateTimeKind.Utc);
        var dates = new[] { start.AddSeconds(-1), start, start, start.AddDays(1).AddSeconds(-1), start.AddDays(1), start.AddDays(2) };
        var statuses = new[] { "Completed", "Completed", "Failed", "Completed", "Running", "Queued" };
        var runs = new List<EvaluationRun>(); Evaluation? anonymous = null;
        for (var i = 0; i < 6 + extraRuns; i++)
        {
            var run = new EvaluationRun
            {
                RunType = i is 3 or 5 ? RunTypes.PromptTest : RunTypes.Screening,
                Status = i < 6 ? statuses[i] : RunStatuses.Completed, PromptVersion = old,
                EvaluationSet = i is 3 or 5 ? set : null, AccuracyPercent = i == 3 ? 75m : null,
                ModelName = "saved-model-A25", Temperature = .42m, KeywordWeight = .4m, AiWeight = .6m, PassThreshold = 81,
                CreatedAt = i < 6 ? dates[i] : start.AddMinutes(i),
                CompletedAt = i is 4 or 5 ? null : (i < 6 ? dates[i] : start.AddMinutes(i)).AddSeconds(12)
            };
            for (var k = 0; k < (i == 1 ? 5 : 1); k++)
            {
                var erased = i == 1 && k == 2;
                var cv = JobCrudEndpointTests.Cv();
                cv.OriginalFileName = erased ? Erased + ".pdf" : $"synthetic-a25-{i}-{k}.pdf";
                cv.Candidate = new Candidate { FullName = erased ? Erased : $"Synthetic Candidate {i}-{k}" };
                cv.AnonymizedAt = erased ? start : null;
                cv.ExtractedText = "SYNTHETIC_TEXT_MUST_NOT_BE_READ_A25";
                cv.ExtractionStatus = i == 1 && k == 0 ? ExtractionStatuses.Failed : ExtractionStatuses.Completed;
                cv.ExtractionError = (erased ? Erased : Secret) + " C:\\private\\file.pdf";
                var evaluation = new Evaluation
                {
                    Application = new Application { Cv = cv, JobId = k == 4 ? 2 : 1, CreatedAt = run.CreatedAt },
                    Status = i == 1 && k is 0 or 2 ? EvaluationStatuses.Failed : i == 1 && k == 3 ? EvaluationStatuses.Pending : EvaluationStatuses.Completed,
                    ScoreSource = i == 1 && k is 1 or 2 ? ScoreSources.KeywordOnly : i == 1 && k == 3 ? null : ScoreSources.Combined,
                    KeywordScore = 60, AiScore = k == 1 ? null : 80, FinalScore = 72, PredictedPass = true,
                    ErrorMessage = erased ? Erased : i == 1 && k == 1 ? "HTTP 429 quota " + Secret : i == 1 && k == 0 ? null : null,
                    AiRawResponse = Secret + " RAW AI", CreatedAt = run.CreatedAt
                };
                run.Evaluations.Add(evaluation); if (erased) anonymous = evaluation;
            }
            run.TotalItems = run.Evaluations.Count; run.ProcessedItems = run.Evaluations.Count(e => e.Status != EvaluationStatuses.Pending);
            run.FailedItems = run.Evaluations.Count(e => e.Status == EvaluationStatuses.Failed);
            db.Add(run); runs.Add(run);
        }
        await db.SaveChangesAsync();
        return new(runs.Select(r => r.RunId).ToArray(), old.PromptVersionId, current.PromptVersionId, set.EvaluationSetId, runs[1].RunId, anonymous!.EvaluationId);
    }

    private static async Task<Seed> SeedFactoryAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync(); return await SeedAsync(db);
    }

    [Fact]
    public async Task Legacy_B7_client_still_reads_arrays_and_page_mode_has_full_saved_metadata()
    {
        using var factory = Factory(); var seed = await SeedFactoryAsync(factory); using var client = factory.CreateClient();
        var b7 = new web::SmartCv.Web.Services.RunApiClient(client, NullLogger<web::SmartCv.Web.Services.RunApiClient>.Instance);
        var list = await b7.ListScreeningAsync(3); Assert.True(list.IsSuccess); Assert.Equal(new[] { seed.Runs[4], seed.Runs[2], seed.Runs[1] }, list.Data!.Select(r => r.RunId));
        using var legacy = JsonDocument.Parse(await client.GetStringAsync("/api/runs")); Assert.Equal(JsonValueKind.Array, legacy.RootElement.ValueKind);
        Assert.Equal(6, legacy.RootElement.GetArrayLength()); Assert.DoesNotContain("systemPrompt", legacy.RootElement.GetRawText());
        var page = (await client.GetFromJsonAsync<RunPageDto>("/api/runs?page=2&pageSize=2"))!;
        Assert.Equal((2, 2, 6, 3), (page.Page, page.PageSize, page.TotalCount, page.TotalPages)); Assert.Equal(new[] { seed.Runs[3], seed.Runs[2] }, page.Items.Select(r => r.RunId));
        var saved = page.Items[0]; Assert.Equal(seed.OldPrompt, saved.PromptVersionId); Assert.Equal("A25 old", saved.PromptVersionName);
        Assert.Equal(("saved-model-A25", .42m, .4m, .6m, 81m), (saved.ModelName, saved.Temperature, saved.KeywordWeight, saved.AiWeight, saved.PassThreshold));
        Assert.Equal(seed.Set, saved.EvaluationSetId); Assert.Equal("A25 historical set", saved.EvaluationSetName); Assert.Equal(75m, saved.AccuracyPercent); Assert.Equal(12, saved.DurationSeconds);
        Assert.Single(saved.Jobs); Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind); Assert.Equal(DateTimeKind.Utc, saved.CompletedAt!.Value.Kind);
        Assert.Null((await client.GetFromJsonAsync<RunPageDto>("/api/runs?page=1"))!.Items.First().DurationSeconds);
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/runs?page=1"));
        Assert.Equal(new[] { "items", "page", "pageSize", "totalCount", "totalPages" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("systemPrompt", json.RootElement.GetRawText()); Assert.DoesNotContain(Secret, json.RootElement.GetRawText());
    }

    [Fact]
    public async Task Combined_filters_day_boundaries_ties_and_empty_pages_are_correct()
    {
        using var factory = Factory(); var seed = await SeedFactoryAsync(factory); using var client = factory.CreateClient();
        var day = (await client.GetFromJsonAsync<RunPageDto>("/api/runs?from=2026-11-17&to=2026-11-17&page=1"))!;
        Assert.Equal(new[] { seed.Runs[3], seed.Runs[2], seed.Runs[1] }, day.Items.Select(r => r.RunId)); Assert.Equal(3, day.TotalCount);
        var combined = (await client.GetFromJsonAsync<RunPageDto>("/api/runs?type=Screening&status=Completed&jobId=2&from=2026-11-17&to=2026-11-17&page=1"))!;
        Assert.Equal(seed.MixedRun, Assert.Single(combined.Items).RunId); Assert.Equal(2, combined.Items.Single().Jobs.Count);
        Assert.Single((await client.GetFromJsonAsync<List<RunSummaryDto>>("/api/runs?type=PromptTest&status=Completed"))!);
        foreach (var query in new[] { "page=100", "page=2147483647&pageSize=1", "page=1&jobId=999999", "page=1&from=2028-01-01", "page=4&pageSize=2" })
        {
            var empty = (await client.GetFromJsonAsync<RunPageDto>("/api/runs?" + query))!; Assert.Empty(empty.Items);
            if (query.Contains("999999") || query.Contains("2028")) Assert.Equal((0, 0), (empty.TotalCount, empty.TotalPages));
        }
        Assert.Equal(2, (await client.GetFromJsonAsync<RunPageDto>("/api/runs?page=3&pageSize=2"))!.Items.Count);
        Assert.Empty((await client.GetFromJsonAsync<List<RunSummaryDto>>("/api/runs?jobId=999999"))!);
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("page=-1", "page")]
    [InlineData("page=oops", "page")]
    [InlineData("page=2147483648", "page")]
    [InlineData("page=2147483647&pageSize=100", "page")]
    [InlineData("page=1&pageSize=0", "pageSize")]
    [InlineData("page=1&pageSize=101", "pageSize")]
    [InlineData("pageSize=20", "pageSize")]
    [InlineData("type=Other", "type")]
    [InlineData("type=", "type")]
    [InlineData("status=Done", "status")]
    [InlineData("jobId=0", "jobId")]
    [InlineData("jobId=abc", "jobId")]
    [InlineData("from=2026-02-29", "from")]
    [InlineData("from=0001-01-01", "from")]
    [InlineData("to=9999-12-31", "to")]
    [InlineData("from=2026-12-02&to=2026-12-01", "from")]
    [InlineData("page=1&PAGE=2", "page")]
    [InlineData("type=Screening&type=PromptTest", "type")]
    [InlineData("status=Queued&status=Running", "status")]
    [InlineData("from=2026-11-17&from=2026-11-18", "from")]
    [InlineData("to=2026-11-17&to=2026-11-18", "to")]
    [InlineData("jobId=1&jobId=2", "jobId")]
    [InlineData("take=1&take=2", "take")]
    [InlineData("page=1&pageSize=1&pageSize=2", "pageSize")]
    public async Task Invalid_query_returns_safe_Vietnamese_ProblemDetails(string query, string field)
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/runs?" + query); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal("Bộ lọc không hợp lệ", json.RootElement.GetProperty("title").GetString());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _)); Assert.DoesNotContain("StackTrace", json.RootElement.GetRawText());
    }

    [Fact]
    public async Task Details_use_referenced_prompt_and_saved_settings_and_failures_do_not_leak_erased_or_raw_errors()
    {
        using var factory = Factory(); var seed = await SeedFactoryAsync(factory); using var client = factory.CreateClient();
        var progress = (await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{seed.MixedRun}"))!;
        Assert.Equal("A25 OLD SAVED SYSTEM PROMPT", progress.SystemPrompt); Assert.Equal(seed.OldPrompt, progress.PromptVersionId); Assert.Equal(5, progress.Items.Count);
        Assert.Equal((.42m, .4m, .6m, 81m), (progress.Temperature, progress.KeywordWeight, progress.AiWeight, progress.PassThreshold));
        var anon = progress.Items.Single(i => i.EvaluationId == seed.AnonymousEvaluation); Assert.Equal(RetentionRules.AnonymizedFileName, anon.FileName); Assert.Null(anon.CandidateName); Assert.Null(anon.ErrorMessage);
        Assert.Null(progress.EvaluationSetId); Assert.Null(progress.AccuracyPercent);
        var test = (await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{seed.Runs[3]}"))!; Assert.Equal(seed.Set, test.EvaluationSetId); Assert.Equal(75m, test.AccuracyPercent);
        using var response = await client.GetAsync($"/api/runs/{seed.MixedRun}/failures"); Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(Erased, text); Assert.DoesNotContain(Secret, text); Assert.DoesNotContain("systemPrompt", text); Assert.DoesNotContain("extractedText", text); Assert.DoesNotContain("aiRawResponse", text);
        var failures = (await response.Content.ReadFromJsonAsync<RunFailuresDto>())!; Assert.Equal((2, 1, 3), (failures.FailedCount, failures.KeywordOnlyCount, failures.Items.Count));
        Assert.Equal(new[] { "Failed", "Failed", "KeywordOnly" }, failures.Items.Select(f => f.Kind));
        Assert.All(failures.Items, f => { Assert.False(string.IsNullOrWhiteSpace(f.ErrorMessage)); Assert.Equal(DateTimeKind.Utc, f.CreatedAt.Kind); });
        Assert.Contains("hạn mức", failures.Items.Last().ErrorMessage);
        var erased = failures.Items.Single(f => f.EvaluationId == seed.AnonymousEvaluation); Assert.Null(erased.CandidateName); Assert.Null(erased.CvExtractionError); Assert.Equal(RetentionRules.AnonymizedFileName, erased.FileName);
        Assert.NotNull(failures.Items[0].CvExtractionError);
        var empty = (await client.GetFromJsonAsync<RunFailuresDto>($"/api/runs/{seed.Runs[0]}/failures"))!; Assert.Empty(empty.Items); Assert.Equal((0, 0), (empty.FailedCount, empty.KeywordOnlyCount));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/999999/failures")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PutAsJsonAsync($"/api/prompts/{seed.OldPrompt}", new { systemPrompt = "changed" })).StatusCode);
        using var created = await client.PostAsJsonAsync("/api/runs", new StartRunRequest { ApplicationIds = [progress.Items[0].ApplicationId] }); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var started = (await created.Content.ReadFromJsonAsync<RunProgressDto>())!; var detail = (await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{started.RunId}"))!;
        Assert.Equal(seed.NewPrompt, detail.PromptVersionId); Assert.Equal("A25 NEW ACTIVE SYSTEM PROMPT", detail.SystemPrompt);
        Assert.Equal("A25 OLD SAVED SYSTEM PROMPT", (await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{seed.MixedRun}"))!.SystemPrompt);
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    public async Task Internal_routes_fail_closed_outside_Development_feature_gate(string environment, bool enabled)
    {
        using var factory = Factory(environment, enabled);
        if (environment != "Development" && enabled) { Assert.Contains("M6", Assert.Throws<InvalidOperationException>(() => factory.CreateClient()).Message); return; }
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-HR", "admin");
        foreach (var path in new[] { "/api/runs", "/api/runs?page=1", "/api/runs/1", "/api/runs/1/failures" }) Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }
}
