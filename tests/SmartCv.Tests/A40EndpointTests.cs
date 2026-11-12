using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;
using Xunit.Abstractions;

namespace SmartCv.Tests;

public class A40EndpointTests(ITestOutputHelper output)
{
    private sealed record Seed(int Run, int OtherRun, int[] Main, int OtherJob, int OtherEvaluation);
    private sealed class NoAi : IAiEvaluator
    {
        public Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Read-only A40 APIs must never call AI.");
    }
    private static WebApplicationFactory<Program> Factory(Dictionary<string,string?>? settings = null) =>
        TestSupport.ApiFactory(settings: new Dictionary<string,string?>(settings ?? []) { ["Scoring:WorkerEnabled"] = "false" })
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => { s.RemoveAll<IAiEvaluator>(); s.AddSingleton<IAiEvaluator, NoAi>(); }));

    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        if (!db.Database.IsRelational()) await db.Database.EnsureCreatedAsync();
        var job = new Job { Title = "A40 synthetic job", MinGpa = 2.5m, MinYearsExperience = 2, Skills =
            [new() { SkillCode = "M1", DisplayName = "Required", SkillType = "MustHave" }, new() { SkillCode = "N1", DisplayName = "Optional", SkillType = "NiceToHave" }] };
        var otherJob = new Job { Title = "Other synthetic job" };
        var prompt = new PromptVersion { VersionName = "A40 fixture", SystemPrompt = "Synthetic only", IsActive = true };
        EvaluationRun Run() => new() { RunType = "Screening", PromptVersion = prompt, ModelName = "fake-not-called", KeywordWeight = .3m,
            AiWeight = .7m, PassThreshold = 70, Status = "Completed", TotalItems = 7, CreatedAt = DateTime.UtcNow };
        var run = Run(); var other = Run();
        Evaluation Item(Job j, int index, EvaluationRun r) => new()
        {
            Run = r, Status = "Completed", KeywordScore = 80, AiScore = 80, FinalScore = 80,
            ScoreSource = "Combined", FitLabel = "Phù hợp", PredictedPass = true, YearsExperience = 3,
            Application = new Application { Job = j, StageVersion = Guid.NewGuid(), Cv = new Cv
            {
                CvId = Guid.NewGuid(), OriginalFileName = $"synthetic-{index}.pdf", StoredPath = "not-a-real-file", ContentType = "application/pdf",
                FileSizeBytes = 1, FileHash = Guid.NewGuid().ToString("N").PadRight(64, '0'), ExtractionStatus = "Completed", ExtractedText = new string('x', 499) + " ",
                Candidate = new Candidate { FullName = $"Synthetic {index}" }
            } }, CreatedAt = DateTime.UtcNow
        };
        var main = Enumerable.Range(0, 6).Select(i => Item(job, i, run)).ToArray();
        var capped = ScoreCombiner.Combine(new(40, [new("M1", "Required", true, false, null)], null, null, []),
            new AiEvaluationResult { IsSuccess = true, Score = 90 }, .3m, .7m, 70, 69);
        main[0].KeywordScore = capped.KeywordScore; main[0].AiScore = capped.AiScore; main[0].FinalScore = capped.FinalScore;
        main[0].Reasoning = capped.Reasoning; main[0].FitLabel = capped.FitLabel; main[0].PredictedPass = capped.PredictedPass;
        main[0].Application.Cv.ExtractedText = new string('Á', 499);
        main[0].Application.Stage = "Rejected"; main[0].Application.RejectReason = "MissingSkills"; main[0].Application.StageUpdatedAt = DateTime.UtcNow;
        main[1].AiScore = null; main[1].ScoreSource = "KeywordOnly"; main[1].FinalScore = 69; main[1].ErrorMessage = "Synthetic AI error";
        main[4].Status = "Pending"; main[4].FinalScore = 99; main[4].Application.Cv.ExtractedText = "short";
        main[5].Status = "Failed"; main[5].FinalScore = 100; main[5].Application.Cv.ExtractedText = null;
        var differentJob = Item(otherJob, 10, run); var differentRun = Item(job, 11, other);
        db.AddRange(main); db.AddRange(differentJob, differentRun);
        await db.SaveChangesAsync();
        db.EvaluationSkills.AddRange(main.Select(e => new EvaluationSkill
        { EvaluationId = e.EvaluationId, JobId = job.JobId, SkillCode = "M1", IsMatched = e != main[0], Evidence = e == main[0] ? null : "Synthetic skill evidence" }));
        db.EvaluationSkills.Add(new() { EvaluationId = main[0].EvaluationId, JobId = job.JobId, SkillCode = "N1", IsMatched = true, Evidence = "Optional synthetic evidence" });
        await db.SaveChangesAsync();
        return new(run.RunId, other.RunId, main.Select(e => e.EvaluationId).ToArray(), differentJob.EvaluationId, differentRun.EvaluationId);
    }

    private static async Task VerifyReadsAsync(HttpClient http, Seed seed)
    {
        var detail = await http.GetFromJsonAsync<EvaluationDetailDto>($"/api/evaluations/{seed.Main[0]}");
        var ranking = (await http.GetFromJsonAsync<RankingResponse>($"/api/runs/{seed.Run}/ranking?jobId={detail!.JobId}"))!;
        Assert.Equal(2, ranking.FlaggedCount); // first CV has THREE flags, not three flagged CVs
        Assert.Equal(1, ranking.StageCounts["Rejected"]); Assert.Equal(5, ranking.StageCounts["New"]);
        var ids = new[] { seed.Main[3], seed.Main[0], seed.Main[1] };
        var compare = (await http.GetFromJsonAsync<CandidateCompareResponse>($"/api/runs/{seed.Run}/compare?evaluationIds={string.Join(',', ids)}"))!;
        Assert.Equal(ids, compare.Candidates.Select(e => e.EvaluationId));
        Assert.Equal(detail.JobId, compare.JobId); Assert.Equal("M1", compare.Skills[0].SkillCode);
        Assert.Equal((2.5m, 2m), (compare.JobMinGpa, compare.JobMinYearsExperience));
        foreach (var item in compare.Candidates)
        {
            var row = ranking.Items.Single(e => e.EvaluationId == item.EvaluationId);
            var d = (await http.GetFromJsonAsync<EvaluationDetailDto>($"/api/evaluations/{item.EvaluationId}"))!;
            Assert.Equal(row.ReviewFlags, item.ReviewFlags); Assert.Equal(d.ReviewFlags, item.ReviewFlags);
            Assert.Equal((row.Rank, row.KeywordScore, row.AiScore, row.FinalScore, row.FitLabel, row.PredictedPass, row.ScoreSource),
                (item.Rank, item.KeywordScore, item.AiScore, item.FinalScore, item.FitLabel, item.PredictedPass, item.ScoreSource));
            Assert.Equal((d.Rank, d.RankTotal, d.Stage, d.RejectReason, d.StageVersion, d.StageUpdatedAt),
                (item.Rank, item.RankTotal, item.Stage, item.RejectReason, item.StageVersion, item.StageUpdatedAt));
            Assert.Equal(4, item.RankTotal);
            Assert.Equal(d.YearsExperience, item.YearsExperience);
        }
        Assert.Equal(499, detail.ExtractedTextLength);
        Assert.Equal(500, (await http.GetFromJsonAsync<EvaluationDetailDto>($"/api/evaluations/{seed.Main[1]}"))!.ExtractedTextLength); // trailing SQL space counts
        Assert.Equal(3, detail.ReviewFlags.Count);
        Assert.Equal("Optional synthetic evidence", compare.Candidates[1].MatchedSkills["N1"]);
        Assert.DoesNotContain("M1", compare.Candidates[1].MatchedSkills.Keys);
        var unfinished = (await http.GetFromJsonAsync<CandidateCompareResponse>($"/api/runs/{seed.Run}/compare?evaluationIds={seed.Main[4]},{seed.Main[5]}"))!;
        Assert.All(unfinished.Candidates, e => { Assert.Null(e.Rank); Assert.Empty(e.ReviewFlags); });
        var text = await http.GetStringAsync($"/api/runs/{seed.Run}/compare?evaluationIds={seed.Main[0]},{seed.Main[2]}");
        Assert.DoesNotContain("extractedText\"", text); // no whole CV text in compare
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        return JsonSerializer.Serialize(new
        {
            Evaluations = await db.Evaluations.AsNoTracking().OrderBy(e => e.EvaluationId).Select(e => new { e.EvaluationId, e.Status, e.KeywordScore, e.AiScore, e.FinalScore, e.FitLabel, e.PredictedPass }).ToListAsync(),
            Stages = await db.Applications.AsNoTracking().OrderBy(a => a.ApplicationId).Select(a => new { a.ApplicationId, a.Stage, a.RejectReason, a.StageVersion, a.StageUpdatedAt }).ToListAsync(),
            History = await db.ApplicationEvents.CountAsync()
        });
    }

    [Fact]
    public async Task Ranking_detail_compare_are_consistent_and_read_only_with_competition_ranks()
    {
        using var f = Factory(); using var http = f.CreateClient(); var seed = await SeedAsync(f);
        var before = await SnapshotAsync(f); await VerifyReadsAsync(http, seed); Assert.Equal(before, await SnapshotAsync(f));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("1")] [InlineData("1,2,3,4")]
    [InlineData("1,1")] [InlineData("0,1")] [InlineData("-1,2")] [InlineData("x,2")]
    [InlineData("1,,2")] [InlineData("1,2,")] [InlineData("2147483648,2")] [InlineData("1.5,2")]
    public async Task Invalid_compare_ids_return_400_ProblemDetails(string? ids)
    {
        using var f = Factory(); using var http = f.CreateClient();
        await AssertProblemAsync(http, "/api/runs/1/compare" + (ids is null ? "" : "?evaluationIds=" + Uri.EscapeDataString(ids)), 400);
    }

    private static async Task AssertProblemAsync(HttpClient http, string url, int status)
    {
        using var response = await http.GetAsync(url); Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32()); Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Missing_resources_and_cross_run_or_job_are_distinguished()
    {
        using var f = Factory(); using var http = f.CreateClient(); var s = await SeedAsync(f);
        await AssertProblemAsync(http, $"/api/runs/999999/compare?evaluationIds={s.Main[0]},{s.Main[1]}", 404);
        await AssertProblemAsync(http, $"/api/runs/{s.Run}/compare?evaluationIds={s.Main[0]},999999", 404);
        await AssertProblemAsync(http, $"/api/runs/{s.Run}/compare?evaluationIds={s.Main[0]},{s.OtherEvaluation}", 400);
        await AssertProblemAsync(http, $"/api/runs/{s.Run}/compare?evaluationIds={s.Main[0]},{s.OtherJob}", 400);
        var valid = await http.GetFromJsonAsync<CandidateCompareResponse>($"/api/runs/{s.Run}/compare?evaluationIds=%20{s.Main[2]}%20,%20{s.Main[0]}%20");
        Assert.Equal(new[] { s.Main[2], s.Main[0] }, valid!.Candidates.Select(c => c.EvaluationId));
    }

    [Theory]
    [InlineData("ReviewFlags:ScoreGap", "0")] [InlineData("ReviewFlags:ScoreGap", "101")]
    [InlineData("ReviewFlags:MinTextLength", "-1")] [InlineData("ReviewFlags:MinTextLength", "0")]
    [InlineData("ReviewFlags:CappedHighAiScore", "-0.01")] [InlineData("ReviewFlags:CappedHighAiScore", "100.01")]
    public void Invalid_configuration_stops_startup(string key, string value)
    {
        using var f = Factory(new() { [key] = value });
        Assert.Throws<OptionsValidationException>(() => f.CreateClient());
    }

    [Fact]
    public async Task Configured_thresholds_change_flags_not_scores_and_access_boundary_is_preserved()
    {
        using var f = Factory(new() { ["ReviewFlags:ScoreGap"] = "60", ["ReviewFlags:MinTextLength"] = "1", ["ReviewFlags:CappedHighAiScore"] = "95" });
        using var http = f.CreateClient(); var s = await SeedAsync(f);
        var detail = (await http.GetFromJsonAsync<EvaluationDetailDto>($"/api/evaluations/{s.Main[0]}"))!;
        Assert.Empty(detail.ReviewFlags); Assert.Equal(69, detail.FinalScore);
        using var disabled = TestSupport.ApiFactory(settings: new() { ["CvUpload:Endpoint:Enabled"] = "false", ["Scoring:WorkerEnabled"] = "false" });
        using var closed = disabled.CreateClient(); Assert.Equal(HttpStatusCode.NotFound, (await closed.GetAsync("/api/runs/1/compare?evaluationIds=1,2")).StatusCode);
        using var prod = TestSupport.ApiFactory("Production", new() { ["CvUpload:Endpoint:Enabled"] = "true", ["Scoring:WorkerEnabled"] = "false" });
        Assert.Contains("xác thực", Assert.Throws<InvalidOperationException>(() => prod.CreateClient()).Message);
    }

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Count); return base.ReaderExecutingAsync(command,eventData,result,cancellationToken); }
    }

    [A40SqlFact]
    public async Task Real_SQL_Server_projects_lengths_flags_and_compare_without_N_plus_one_or_writes()
    {
        var sql = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A40SqlFactAttribute.Setting));
        A40SqlFactAttribute.Validate(sql); sql.InitialCatalog = "SmartCv_A40_Test_" + Guid.NewGuid().ToString("N");
        sql.Pooling = false; sql.ConnectTimeout = 10;
        var evidence = TestSupport.RepoFile("artifacts/a40-tests/retained-sql-databases.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await File.AppendAllTextAsync(evidence, sql.InitialCatalog + Environment.NewLine);
        var counter = new ReadCounter();
        using var f = Factory().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<SmartCvDbContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            services.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(sql.ConnectionString).AddInterceptors(counter));
        }));
        using var http = f.CreateClient();
        using (var scope = f.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().Database.MigrateAsync();
        output.WriteLine("Retained isolated A40 test database: " + sql.InitialCatalog);
        var s = await SeedAsync(f); var before = await SnapshotAsync(f); await VerifyReadsAsync(http, s);
        var queryCounts = new List<int>();
        foreach (var ids in new[] { new[] { s.Main[0],s.Main[1] }, new[] { s.Main[0],s.Main[1],s.Main[2] } })
        {
            counter.Count = 0; using var response = await http.GetAsync($"/api/runs/{s.Run}/compare?evaluationIds={string.Join(',',ids)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.InRange(counter.Count, 1, 10);
            queryCounts.Add(counter.Count); output.WriteLine($"Compare {ids.Length} candidates: {counter.Count} SQL reads.");
        }
        Assert.Equal(queryCounts[0], queryCounts[1]);
        Assert.Equal(before, await SnapshotAsync(f));
    }
}

public sealed class A40SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A40_TEST_SQL_CONNECTION";
    public A40SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value)) Skip = $"Set {Setting} for an isolated, retained SQL Server A40 database.";
        else Validate(new SqlConnectionStringBuilder(value));
    }
    internal static void Validate(SqlConnectionStringBuilder sql)
    {
        if (!sql.InitialCatalog.StartsWith("SmartCv_A40_Test_", StringComparison.Ordinal) || !string.IsNullOrEmpty(sql.AttachDBFilename))
            throw new ArgumentException("A40 SQL tests require a SmartCv_A40_Test_ catalog without AttachDBFilename.");
    }
}
