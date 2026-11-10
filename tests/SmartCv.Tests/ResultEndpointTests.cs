using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

public class ResultEndpointTests
{
    private static async Task<int> SeedAsync(IServiceProvider services, string status = "Running")
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        var job = await db.Jobs.Include(j => j.Skills).FirstAsync();
        var run = new EvaluationRun
        {
            RunType = "Screening", Status = status, TotalItems = 7, ProcessedItems = 5, FailedItems = 1,
            PromptVersion = new PromptVersion { VersionName = "historical", SystemPrompt = "saved" },
            ModelName = "historical-model", Temperature = .2m, KeywordWeight = .4m, AiWeight = .6m,
            PassThreshold = 75, CreatedAt = DateTime.UtcNow
        };
        var scores = new decimal?[] { 95, 80, 80, 60, 40, 999, null };
        var labels = new[] { FitLevels.Excellent, FitLevels.Good, FitLevels.Good, FitLevels.Potential, FitLevels.Poor };
        for (var i = 0; i < scores.Length; i++)
        {
            var evaluation = new Evaluation
            {
                Application = new Application { JobId = job.JobId, Cv = new Cv
                {
                    CvId = Guid.NewGuid(), OriginalFileName = $"cv-{i}.pdf", StoredPath = "test",
                    ContentType = "application/pdf", FileHash = i.ToString().PadLeft(64, 'a'),
                    ExtractionStatus = i == 6 ? "Pending" : "Completed"
                } },
                Status = i == 5 ? "Failed" : i == 6 ? "Pending" : "Completed",
                FinalScore = scores[i], KeywordScore = i < 5 ? scores[i] : null,
                AiScore = i < 4 ? scores[i] : null, ScoreSource = i == 4 ? "KeywordOnly" : "Combined",
                FitLabel = i < 5 ? labels[i] : null, PredictedPass = i < 3,
                ErrorMessage = i == 4 ? "Gemini quota" : i == 5 ? "Extraction failed" : null,
                AiRawResponse = i == 0 ? "{\"gpa\":3.2}" : null
            };
            if (i == 0)
                evaluation.Skills.Add(new EvaluationSkill
                {
                    JobId = job.JobId, SkillCode = job.Skills.First().SkillCode,
                    IsMatched = true, Evidence = "Evidence from CV"
                });
            run.Evaluations.Add(evaluation);
        }
        db.Add(run);
        await db.SaveChangesAsync();
        return run.RunId;
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Completed")]
    public async Task Progress_preserves_snapshot_and_distinguishes_item_states(string status)
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory.Services, status);
        var dto = (await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{id}"))!;
        Assert.Equal(status, dto.Status);
        Assert.Equal((7, 5, 1), (dto.TotalItems, dto.ProcessedItems, dto.FailedItems));
        Assert.InRange(dto.Percent, 71m, 72m);
        Assert.Equal(7, dto.Items.Count);
        Assert.Equal("Pending", dto.Items.Last().ExtractionStatus);
        Assert.Contains(dto.Items, i => i.ScoreSource == "KeywordOnly" && i.ErrorMessage == "Gemini quota");
        Assert.Equal(("historical", "historical-model", .4m, .6m),
            (dto.PromptVersionName, dto.ModelName, dto.KeywordWeight, dto.AiWeight));
    }

    [Fact]
    public async Task Ranking_and_detail_agree_on_ties_failures_skills_and_snapshot()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory.Services);
        var ranking = (await client.GetFromJsonAsync<RankingResponse>($"/api/runs/{id}/ranking"))!;
        Assert.Equal(new int?[] { 1, 2, 2, 4, 5, null, null }, ranking.Items.Select(i => i.Rank));
        Assert.Equal(4, ranking.LevelCounts.Count);
        Assert.Equal(2, ranking.LevelCounts[FitLevels.Good]);
        Assert.Equal(5, ranking.LevelCounts.Values.Sum());
        Assert.Equal(3, ranking.PassCount);
        Assert.Equal("KeywordOnly", ranking.Items[4].ScoreSource);
        Assert.Null(ranking.Items[4].AiScore);
        foreach (var item in ranking.Items)
        {
            var detail = (await client.GetFromJsonAsync<EvaluationDetailDto>($"/api/evaluations/{item.EvaluationId}"))!;
            Assert.Equal(item.Rank, detail.Rank);
            Assert.Equal(5, detail.RankTotal);
            Assert.Equal(("historical", "historical-model", .2m, .4m, .6m, 75m),
                (detail.PromptVersionName, detail.ModelName, detail.Temperature, detail.KeywordWeight, detail.AiWeight, detail.PassThreshold));
            Assert.Null(detail.YearsExperience);
            Assert.Null(detail.CandidateName);
            if (item.Rank == 1)
            {
                Assert.Equal(3.2m, detail.Gpa);
                Assert.Contains(detail.Skills, s => s.IsMatched && s.Evidence == "Evidence from CV");
                Assert.NotNull(detail.AiRawResponse);
            }
            else Assert.Null(detail.Gpa);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/runs/{id}/ranking?jobId=99999")).StatusCode);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(101)]
    public async Task List_filters_latest_and_take_are_consistent(int take)
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory.Services);
        var runs = (await client.GetFromJsonAsync<List<RunSummaryDto>>($"/api/runs?type=Screening&take={take}"))!;
        var run = Assert.Single(runs);
        Assert.Equal(id, run.RunId);
        Assert.Equal(7, Assert.Single(run.Jobs).Count);
        var filtered = await client.GetFromJsonAsync<List<RunSummaryDto>>($"/api/runs?jobId={run.Jobs[0].JobId}");
        Assert.Single(filtered!);
        Assert.Empty((await client.GetFromJsonAsync<List<RunSummaryDto>>("/api/runs?type=PromptTest"))!);
        Assert.Empty((await client.GetFromJsonAsync<List<RunSummaryDto>>("/api/runs?jobId=99999"))!);
        var latest = await client.GetFromJsonAsync<RunSummaryDto>($"/api/runs/latest?jobId={run.Jobs[0].JobId}");
        Assert.Equal(id, latest!.RunId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/latest?jobId=99999")).StatusCode);
    }

    [Fact]
    public async Task List_orders_multiple_runs_clamps_take_and_latest_excludes_prompt_tests()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var original = await db.EvaluationRuns.SingleAsync();
            for (var i = 0; i < 102; i++)
                db.Add(new EvaluationRun
                {
                    RunType = "PromptTest", PromptVersionId = original.PromptVersionId,
                    ModelName = "test", CreatedAt = original.CreatedAt.AddMinutes(i + 1)
                });
            await db.SaveChangesAsync();
        }
        var latest = (await client.GetFromJsonAsync<RunSummaryDto>("/api/runs/latest"))!;
        Assert.Equal(id, latest.RunId);
        var capped = (await client.GetFromJsonAsync<List<RunSummaryDto>>("/api/runs?take=500"))!;
        Assert.Equal(100, capped.Count);
        Assert.True(capped.Zip(capped.Skip(1)).All(pair => pair.First.CreatedAt >= pair.Second.CreatedAt));
        Assert.Single((await client.GetFromJsonAsync<List<RunSummaryDto>>("/api/runs?take=-1"))!);
    }

    [Fact]
    public async Task Empty_level_counts_and_malformed_saved_json_are_safe()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = await SeedAsync(factory.Services);
        int evaluationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var evaluations = await db.Evaluations.ToListAsync();
            evaluationId = evaluations[0].EvaluationId;
            foreach (var e in evaluations)
            {
                e.Status = "Pending";
                e.AiRawResponse = "malformed";
            }
            await db.SaveChangesAsync();
        }
        var ranking = (await client.GetFromJsonAsync<RankingResponse>($"/api/runs/{id}/ranking"))!;
        Assert.Equal(4, ranking.LevelCounts.Count);
        Assert.All(ranking.LevelCounts.Values, count => Assert.Equal(0, count));
        Assert.All(ranking.Items, item => Assert.Null(item.Rank));
        Assert.Equal(0, ranking.PassCount);
        var detail = (await client.GetFromJsonAsync<EvaluationDetailDto>($"/api/evaluations/{evaluationId}"))!;
        Assert.Null(detail.Gpa);
        Assert.Equal(0, detail.RankTotal);
    }

    [Theory]
    [InlineData("/api/runs/latest")]
    [InlineData("/api/runs/99999")]
    [InlineData("/api/runs/99999/ranking")]
    [InlineData("/api/evaluations/99999")]
    public async Task Missing_resources_return_404(string path)
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", false)]
    public async Task Results_keep_existing_personal_data_access_boundary(string environment, bool enabled)
    {
        using var factory = TestSupport.ApiFactory(environment, new() { ["CvUpload:Endpoint:Enabled"] = enabled.ToString() });
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/api/runs", "/api/runs/latest", "/api/runs/1", "/api/runs/1/ranking", "/api/evaluations/1" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }
}
