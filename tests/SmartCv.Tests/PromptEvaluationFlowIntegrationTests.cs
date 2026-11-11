extern alias web;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

public class PromptEvaluationFlowIntegrationTests
{
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory() => TestSupport.ApiFactory(settings: new()
    { ["Scoring:WorkerEnabled"] = "false", ["Gemini:UseFake"] = "true" });
    private static PromptApiClient Api(HttpClient http) => new(http, NullLogger<PromptApiClient>.Instance);
    private static async Task<int[]> SeedApplications(IServiceProvider services)
    {
        using var scope = services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        var job = await db.Jobs.Select(j => j.JobId).FirstAsync();
        var apps = Enumerable.Range(0, 3).Select(i => new Application { JobId = job, Cv = new Cv
        { CvId = Guid.NewGuid(), OriginalFileName = $"fixture-{i}.pdf", StoredPath = "fixture", FileHash = Guid.NewGuid().ToString("N").PadRight(64,'0'), ContentType = "application/pdf",
          ExtractionStatus = i == 2 ? "Pending" : "Completed", ExtractedText = i == 2 ? null : "C# ASP.NET Core SQL Server Git 2 years experience" } }).ToArray();
        db.AddRange(apps); db.AddRange(new PromptVersion { VersionName = "fixture-v1", SystemPrompt = "Quy tắc một" }, new PromptVersion { VersionName = "fixture-v2", SystemPrompt = "Quy tắc hai" });
        await db.SaveChangesAsync(); return apps.Select(a => a.ApplicationId).ToArray();
    }
    private sealed class NoNetworkAi : IAiEvaluator
    {
        public int Calls;
        public Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken ct = default)
        { Calls++; return Task.FromResult(new AiEvaluationResult { IsSuccess = false, ErrorMessage = "Fixture quota exhausted" }); }
    }
    [Fact]
    public async Task Empty_database_to_labeled_set_two_runs_worker_compare_mismatch_and_reload_use_real_api_contracts()
    {
        using var factory = Factory(); using var http = factory.CreateClient(); var api = Api(http);
        Assert.Empty((await api.GetEvaluationSetsAsync()).Data!);
        var apps = await SeedApplications(factory.Services);
        var workspace = new EvaluationSetWorkspace(api) { Name = "Bộ CV fixture", Description = "Isolated test only" };
        await workspace.LoadAsync(default); await workspace.CreateAsync(default);
        Assert.True(workspace.SetId > 0); Assert.Equal(3, workspace.Rows.Count);
        Assert.All(workspace.Rows, row => Assert.Equal("", row.Choice));
        Assert.False(workspace.Rows.Single(r => r.Candidate.ApplicationId == apps[2]).Eligible);
        workspace.Rows[0].Choice = "pass"; workspace.Rows[1].Choice = "fail"; workspace.Rows[2].Choice = "pass";
        workspace.LabeledBy = "HR fixture"; Assert.True(await workspace.SaveAsync(default), workspace.Error);
        var reloaded = new EvaluationSetWorkspace(api); await reloaded.LoadAsync(default); await reloaded.SelectAsync(workspace.SetId, default);
        Assert.Equal("pass", reloaded.Rows[0].Choice); Assert.Equal("fail", reloaded.Rows[1].Choice); Assert.Equal("", reloaded.Rows[2].Choice);
        var set = Assert.Single((await api.GetEvaluationSetsAsync()).Data!);
        Assert.Equal((2,1,0), (set.LabelCount,set.PassCount,set.UnreadyCount));
        List<int> versions;
        using (var scope = factory.Services.CreateScope()) versions = await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().PromptVersions.Where(v => v.VersionName.StartsWith("fixture-v")).OrderBy(v => v.VersionName).Select(v => v.PromptVersionId).ToListAsync();
        var runIds = new List<int>(); var fake = new NoNetworkAi();
        foreach (var version in versions)
        {
            var flow = new PromptTestFlow(api); await flow.RestoreAsync(null, default); await flow.StartAsync(version, set.EvaluationSetId, default);
            Assert.Null(flow.Error); Assert.Equal("Queued", flow.Current!.Status); Assert.Null(flow.Current.CorrectCount);
            runIds.Add(flow.Current.RunId);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
                await new RunScoringProcessor(db, fake, Options.Create(new ScoringOptions()), NullLogger<RunScoringProcessor>.Instance).ProcessRunAsync(flow.Current.RunId, default);
            }
            await flow.RefreshAsync(default); Assert.Equal("Completed", flow.Current!.Status); Assert.NotNull(flow.Current.CorrectCount);
            Assert.Equal(2, flow.Current.TotalLabeledCount);
        }
        Assert.Equal(4, fake.Calls); // Only the in-process stub, never Gemini.
        var summaries = (await api.GetAccuracyRunsAsync(set.EvaluationSetId)).Data!;
        Assert.Equal(2, AccuracyComparisonView.Latest(summaries, set.EvaluationSetId).Count);
        var comparison = await api.CompareAccuracyRunsAsync(runIds[0],runIds[1]); Assert.True(comparison.IsSuccess); Assert.True(comparison.Data!.IsFairComparison);
        Assert.Contains(comparison.Data.Warnings, w => w.Contains("từ khóa", StringComparison.OrdinalIgnoreCase));
        var detail = (await api.GetMismatchRunAsync(runIds[1])).Data!; var view = MismatchView.Create(detail);
        Assert.Equal(2, view.Compared); Assert.Equal(detail.MissedCount, view.FalseNegative); Assert.Equal(detail.WrongPickCount, view.FalsePositive);
        Assert.Equal(detail.CorrectCount, view.TruePositive + view.TrueNegative);
        Assert.Equal(detail.AccuracyPercent, decimal.Round((view.TruePositive + view.TrueNegative)*100m/detail.TotalLabeledCount!.Value,2,MidpointRounding.AwayFromZero));
        foreach (var item in view.Mismatches)
        { var result = await api.GetMismatchEvaluationAsync(item.EvaluationId); Assert.True(result.IsSuccess); Assert.True(MismatchView.SupportsDetail(detail.RunId,item,result.Data!)); }
        var restore = new PromptTestFlow(api); await restore.RestoreAsync(runIds[1].ToString(),default); Assert.Equal(runIds[1],restore.Current!.RunId);
        using var verify = factory.Services.CreateScope(); Assert.Equal(2, await verify.ServiceProvider.GetRequiredService<SmartCvDbContext>().EvaluationRuns.CountAsync());
    }
    [Theory]
    [InlineData("Pending", "CV")][InlineData("Failed", "CV")][InlineData("Completed", "   ")]
    public async Task Start_rejects_unready_labeled_cv_before_queue_or_quota_and_list_explains_it(string status,string text)
    {
        using var factory = Factory(); using var http = factory.CreateClient(); var apps = await SeedApplications(factory.Services);
        var set = (await (await http.PostAsJsonAsync("/api/evaluation-sets",new CreateEvaluationSetRequest { Name="Unready fixture" })).Content.ReadFromJsonAsync<EvaluationSetDto>())!;
        await http.PutAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels",new SaveHrLabelsRequest {LabeledBy="HR fixture",Labels=[new(){ApplicationId=apps[0],IsPass=true},new(){ApplicationId=apps[1],IsPass=false}]});
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var cv = await db.Applications.Where(a=>a.ApplicationId==apps[0]).Select(a=>a.Cv).SingleAsync(); cv.ExtractionStatus=status;cv.ExtractedText=text;await db.SaveChangesAsync();
        var version = await db.PromptVersions.Select(v=>v.PromptVersionId).FirstAsync();
        Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsJsonAsync("/api/prompt-tests",new StartPromptTestRequest {PromptVersionId=version,EvaluationSetId=set.EvaluationSetId})).StatusCode);
        Assert.Equal(1,Assert.Single((await Api(http).GetEvaluationSetsAsync()).Data!).UnreadyCount);
        Assert.Empty(await db.EvaluationRuns.ToListAsync());
    }
}
