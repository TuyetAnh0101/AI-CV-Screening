using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

public class RunSnapshotEndpointTests
{
    [Fact]
    public async Task Creates_atomic_snapshot_then_new_run_uses_new_prompt_and_defaults()
    {
        var name = "A12-" + Guid.NewGuid().ToString("N");
        using var first = TestSupport.ApiFactory(dbName: name);
        using var client = first.CreateClient();
        int applicationId, promptId;
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            await db.Database.EnsureCreatedAsync();
            var prompt = new PromptVersion { VersionName = "v1", SystemPrompt = "FIRST", IsActive = true };
            var cv = new Cv { CvId = Guid.NewGuid(), OriginalFileName = "synthetic.pdf", StoredPath = "synthetic", ContentType = "application/pdf", FileHash = new string('A', 64) };
            var application = new Application { Cv = cv, JobId = 3 };
            db.AddRange(prompt, application);
            await db.SaveChangesAsync();
            applicationId = application.ApplicationId;
            promptId = prompt.PromptVersionId;
        }
        using var response = await client.PostAsJsonAsync("/api/runs", new StartRunRequest { ApplicationIds = [applicationId, applicationId] });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var old = (await response.Content.ReadFromJsonAsync<RunProgressDto>())!;
        Assert.Equal((promptId, 0.3m, 0.7m, 70m, 1, 0), (old.PromptVersionId, old.KeywordWeight, old.AiWeight, old.PassThreshold, old.TotalItems, old.ProcessedItems));
        Assert.Equal("Queued", old.Status);
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var run = await db.EvaluationRuns.Include(r => r.Evaluations).SingleAsync();
            Assert.Equal((old.ModelName, old.Temperature), (run.ModelName, run.Temperature));
            Assert.Equal("Pending", Assert.Single(run.Evaluations).Status);
            (await db.PromptVersions.SingleAsync()).IsActive = false;
            db.PromptVersions.Add(new PromptVersion { VersionName = "v2", SystemPrompt = "SECOND", IsActive = true });
            await db.SaveChangesAsync();
        }
        using var second = TestSupport.ApiFactory(dbName: name, settings: new()
        {
            ["Scoring:ModelName"] = "test-model-v2", ["Scoring:Temperature"] = "0.6",
            ["Scoring:KeywordWeight"] = "0.4", ["Scoring:AiWeight"] = "0.6", ["Scoring:PassThreshold"] = "80"
        });
        using var nextClient = second.CreateClient();
        using var nextResponse = await nextClient.PostAsJsonAsync("/api/runs", new StartRunRequest { ApplicationIds = [applicationId] });
        Assert.Equal(HttpStatusCode.Created, nextResponse.StatusCode);
        var next = (await nextResponse.Content.ReadFromJsonAsync<RunProgressDto>())!;
        Assert.NotEqual(old.PromptVersionId, next.PromptVersionId);
        Assert.Equal(("v2", "test-model-v2", 0.6m, 0.4m, 0.6m, 80m),
            (next.PromptVersionName, next.ModelName, next.Temperature, next.KeywordWeight, next.AiWeight, next.PassThreshold));
        using var checkScope = second.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var savedOld = await checkDb.EvaluationRuns.AsNoTracking().SingleAsync(r => r.RunId == old.RunId);
        Assert.Equal((old.PromptVersionId, old.ModelName, old.Temperature, old.KeywordWeight, old.AiWeight, old.PassThreshold),
            (savedOld.PromptVersionId, savedOld.ModelName, savedOld.Temperature, savedOld.KeywordWeight, savedOld.AiWeight, savedOld.PassThreshold));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejects_empty_or_unknown_applications_without_saving_run(bool empty)
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/runs", new StartRunRequest { ApplicationIds = empty ? [] : [999] });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().EvaluationRuns.ToListAsync());
    }

    [Fact]
    public async Task Run_endpoints_are_hidden_when_upload_endpoints_are_disabled()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["CvUpload:Endpoint:Enabled"] = "false" });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/runs", new StartRunRequest())).StatusCode);
    }
}
