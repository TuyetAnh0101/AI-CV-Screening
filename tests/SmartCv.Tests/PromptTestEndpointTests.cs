using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

public class PromptTestEndpointTests
{
    private static WebApplicationFactory<Program> Factory(Dictionary<string, string?>? settings = null,
        string environment = "Development")
    {
        settings ??= [];
        settings["Scoring:WorkerEnabled"] = "false";
        settings["Gemini:UseFake"] = (environment == "Development").ToString();
        return TestSupport.ApiFactory(environment, settings)
            .WithWebHostBuilder(host => host.ConfigureLogging(logging => logging.ClearProviders()));
    }

    private sealed record Ids(int Prompt, int ActivePrompt, int Set, int[] Applications);

    private static async Task<Ids> SeedAsync(IServiceProvider services, params bool[] labels)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        var active = new PromptVersion { VersionName = "active", SystemPrompt = "ACTIVE", IsActive = true };
        var selected = new PromptVersion { VersionName = "selected", SystemPrompt = "SELECTED", IsActive = false };
        var set = new EvaluationSet { Name = "Synthetic set", CreatedAt = DateTime.UtcNow };
        var jobId = await db.Jobs.Select(j => j.JobId).FirstAsync();
        var apps = Enumerable.Range(0, Math.Max(3, labels.Length)).Select(i => new Application
        {
            JobId = jobId, Cv = new Cv
            {
                CvId = Guid.NewGuid(), OriginalFileName = $"synthetic-{i}.pdf", StoredPath = "synthetic",
                ContentType = "application/pdf", FileHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
                ExtractionStatus = "Completed", ExtractedText = "Synthetic CV content."
            }
        }).ToArray();
        db.AddRange(active, selected, set);
        db.AddRange(apps);
        await db.SaveChangesAsync();
        for (var i = 0; i < labels.Length; i++)
            db.HrLabels.Add(new HrLabel
            {
                EvaluationSetId = set.EvaluationSetId, ApplicationId = apps[i].ApplicationId,
                IsPass = labels[i], LabeledBy = "Synthetic HR", LabeledAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        return new(selected.PromptVersionId, active.PromptVersionId, set.EvaluationSetId, apps.Select(a => a.ApplicationId).ToArray());
    }

    [Fact]
    public async Task Start_queues_only_labeled_applications_with_requested_prompt_and_precise_settings_snapshot()
    {
        using var factory = Factory(new()
        {
            ["Scoring:ModelName"] = "snapshot-model", ["Scoring:Temperature"] = "0.56",
            ["Scoring:KeywordWeight"] = "0.4", ["Scoring:AiWeight"] = "0.6", ["Scoring:PassThreshold"] = "75"
        });
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services, true, false);
        using var response = await client.PostAsJsonAsync("/api/prompt-tests", new StartPromptTestRequest
        {
            PromptVersionId = ids.Prompt, EvaluationSetId = ids.Set
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<PromptTestRunDto>())!;
        Assert.Equal((ids.Prompt, "Queued", 2, 0, 0), (dto.PromptVersionId, dto.Status, dto.TotalItems, dto.ProcessedItems, dto.FailedItems));
        Assert.Equal(("snapshot-model", .56m, .4m, .6m, 75m), (dto.ModelName, dto.Temperature, dto.KeywordWeight, dto.AiWeight, dto.PassThreshold));
        Assert.Equal($"/api/prompt-tests/{dto.RunId}", response.Headers.Location!.OriginalString);
        Assert.Null(dto.AccuracyPercent);
        Assert.Equal("NotCalculated", dto.AccuracyStatus);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var run = await db.EvaluationRuns.Include(r => r.Evaluations).SingleAsync();
        Assert.Equal("PromptTest", run.RunType);
        Assert.Equal(ids.Applications.Take(2).Order(), run.Evaluations.Select(e => e.ApplicationId).Order());
        Assert.All(run.Evaluations, e => Assert.Equal("Pending", e.Status));
        Assert.Equal(ids.ActivePrompt, (await db.PromptVersions.SingleAsync(p => p.IsActive)).PromptVersionId);
        Assert.Equal("SELECTED", (await db.PromptVersions.SingleAsync(p => p.PromptVersionId == ids.Prompt)).SystemPrompt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Start_requires_both_label_types_without_saving_partial_run(int scenario)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var labels = scenario == 0 ? Array.Empty<bool>() : scenario == 1 ? new[] { true } : new[] { false };
        var ids = await SeedAsync(factory.Services, labels);
        using var response = await client.PostAsJsonAsync("/api/prompt-tests", new StartPromptTestRequest { PromptVersionId = ids.Prompt, EvaluationSetId = ids.Set });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.Empty(await db.EvaluationRuns.ToListAsync());
        Assert.Empty(await db.Evaluations.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_reports_missing_prompt_or_set(bool missingPrompt)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services, true, false);
        using var response = await client.PostAsJsonAsync("/api/prompt-tests", new StartPromptTestRequest
        {
            PromptVersionId = missingPrompt ? 999999 : ids.Prompt, EvaluationSetId = missingPrompt ? ids.Set : 999999
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    public async Task Duplicate_active_run_returns_409_with_existing_run_id_but_completed_run_allows_rerun(string status)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services, true, false);
        var request = new StartPromptTestRequest { PromptVersionId = ids.Prompt, EvaluationSetId = ids.Set };
        using var first = await client.PostAsJsonAsync("/api/prompt-tests", request);
        var run = (await first.Content.ReadFromJsonAsync<PromptTestRunDto>())!;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            (await db.EvaluationRuns.SingleAsync()).Status = status;
            await db.SaveChangesAsync();
        }
        using var duplicate = await client.PostAsJsonAsync("/api/prompt-tests", request);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var error = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(run.RunId, error.RootElement.GetProperty("runId").GetInt32());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            (await db.EvaluationRuns.SingleAsync()).Status = "Completed";
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/prompt-tests", request)).StatusCode);
    }

    [Fact]
    public async Task Running_progress_preserves_results_and_removed_labels_without_publishing_final_accuracy()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services, true, false);
        using var created = await client.PostAsJsonAsync("/api/prompt-tests", new StartPromptTestRequest { PromptVersionId = ids.Prompt, EvaluationSetId = ids.Set });
        var runId = (await created.Content.ReadFromJsonAsync<PromptTestRunDto>())!.RunId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var run = await db.EvaluationRuns.Include(r => r.Evaluations).SingleAsync();
            run.Status = "Running";
            run.ProcessedItems = 1;
            var evaluation = run.Evaluations.First(e => e.ApplicationId == ids.Applications[0]);
            evaluation.Status = "Completed"; evaluation.KeywordScore = 80; evaluation.AiScore = 90;
            evaluation.FinalScore = 87; evaluation.PredictedPass = true; evaluation.ScoreSource = "Combined";
            db.HrLabels.Remove(await db.HrLabels.SingleAsync(h => h.ApplicationId == evaluation.ApplicationId));
            db.EvaluationRuns.Add(new EvaluationRun { RunType = "Screening", PromptVersionId = ids.Prompt, Status = "Queued" });
            await db.SaveChangesAsync();
        }
        var detail = (await client.GetFromJsonAsync<PromptTestProgressDto>($"/api/prompt-tests/{runId}"))!;
        Assert.Equal(50, detail.Percent);
        Assert.Equal(2, detail.Items.Count);
        var completed = detail.Items.Single(i => i.ApplicationId == ids.Applications[0]);
        Assert.Null(completed.HrIsPass);
        Assert.Equal((80m, 90m, 87m, "Combined"), (completed.KeywordScore, completed.AiScore, completed.FinalScore, completed.ScoreSource));
        Assert.Null(detail.AccuracyPercent);
        Assert.Single((await client.GetFromJsonAsync<List<PromptTestRunDto>>($"/api/prompt-tests?evaluationSetId={ids.Set}"))!);
        Assert.Empty((await client.GetFromJsonAsync<List<PromptTestRunDto>>("/api/prompt-tests?evaluationSetId=999999"))!);
        using var json = JsonDocument.Parse(await client.GetStringAsync($"/api/prompt-tests/{runId}"));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("correctCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("items")[0].GetProperty("isMatch").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("items")[0].GetProperty("mismatchType").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/prompt-tests/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/prompt-tests/compare")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/prompts/compare")).StatusCode);
    }

    [Theory]
    [InlineData("Scoring:ModelName", " ")]
    [InlineData("Scoring:Temperature", "0.123")]
    [InlineData("Scoring:Temperature", "10")]
    [InlineData("Scoring:PassThreshold", "77.123")]
    public async Task Unpersistable_settings_return_500_without_creating_run(string key, string value)
    {
        using var factory = Factory(new() { [key] = value });
        using var client = factory.CreateClient();
        var ids = await SeedAsync(factory.Services, true, false);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsJsonAsync("/api/prompt-tests",
            new StartPromptTestRequest { PromptVersionId = ids.Prompt, EvaluationSetId = ids.Set })).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().EvaluationRuns.ToListAsync());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Prompt_test_endpoints_keep_existing_access_boundary(string environment)
    {
        using var factory = Factory(new() { ["CvUpload:Endpoint:Enabled"] = "false" }, environment);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/prompt-tests")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/prompt-tests/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/prompt-tests", new StartPromptTestRequest())).StatusCode);
    }
}
