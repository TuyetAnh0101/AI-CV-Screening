using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartCv.Api.Background;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

/// <summary>A11: worker chạy trong host thật (TestServer), database InMemory, AI GIẢ của A10, không gọi Gemini.</summary>
public class RunScoringWorkerTests
{
    private const int BackendJob = 1;
    private const string FullCv = "GPA 3.2. 1.5 năm làm C#, ASP.NET Core, SQL Server, EF Core, Git, Docker.";

    private static Dictionary<string, string?> Settings(bool enabled = true, int fakeDelayMs = 0) => new()
    {
        ["Scoring:WorkerEnabled"] = enabled ? "true" : "false",
        ["Scoring:PollIntervalSeconds"] = "1",
        ["Gemini:UseFake"] = "true",
        ["Gemini:FakeDelayMs"] = fakeDelayMs.ToString()
    };

    private static async Task<T> DbAsync<T>(WebApplicationFactory<Program> factory, Func<SmartCvDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
    }

    private static Task<int> SeedRunAsync(WebApplicationFactory<Program> factory, params (string Text, string Extraction)[] items) => DbAsync(factory, async db =>
    {
        await db.Database.EnsureCreatedAsync();
        var prompt = await db.PromptVersions.FirstOrDefaultAsync();
        if (prompt is null)
        {
            prompt = new PromptVersion { VersionName = "v", SystemPrompt = "HR", IsActive = true, CreatedAt = DateTime.UtcNow };
            db.PromptVersions.Add(prompt);
            await db.SaveChangesAsync();
        }

        var run = new EvaluationRun
        {
            RunType = RunTypes.Screening, PromptVersionId = prompt.PromptVersionId, ModelName = "m", Temperature = 0.2m, KeywordWeight = 0.3m, AiWeight = 0.7m,
            TotalItems = items.Length, CreatedAt = DateTime.UtcNow
        };
        foreach (var (text, extraction) in items)
        {
            var cvId = Guid.NewGuid();
            db.Cvs.Add(new Cv
            {
                CvId = cvId, OriginalFileName = "cv.pdf", StoredPath = $"Uploads/CV/{cvId}.pdf", ContentType = "application/pdf", FileSizeBytes = 1,
                FileHash = cvId.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = extraction, ExtractedText = text, UploadedAt = DateTime.UtcNow
            });
            var application = new Application { CvId = cvId, JobId = BackendJob, CreatedAt = DateTime.UtcNow };
            db.Applications.Add(application);
            run.Evaluations.Add(new Evaluation { Application = application, CreatedAt = DateTime.UtcNow });
        }
        db.EvaluationRuns.Add(run);
        await db.SaveChangesAsync();
        return run.RunId;
    });

    private static async Task<EvaluationRun> WaitForRunAsync(WebApplicationFactory<Program> factory, int runId, Func<EvaluationRun, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var run = await DbAsync(factory, db => db.EvaluationRuns.AsNoTracking().SingleAsync(r => r.RunId == runId));
            if (condition(run)) return run;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Lượt {runId}: {run.Status} {run.ProcessedItems}/{run.TotalItems}");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Enabled_worker_scores_a_run_with_fake_ai()
    {
        using var factory = TestSupport.ApiFactory(settings: Settings());
        var runId = await SeedRunAsync(factory, (FullCv, ExtractionStatuses.Completed), ("", ExtractionStatuses.Failed));

        var run = await WaitForRunAsync(factory, runId, r => r.Status == RunStatuses.Completed);

        Assert.Equal((2, 1), (run.ProcessedItems, run.FailedItems));
        Assert.NotNull(run.CompletedAt);
        var rows = await DbAsync(factory, db => db.Evaluations.AsNoTracking().Where(e => e.RunId == runId).OrderBy(e => e.EvaluationId).ToListAsync());
        Assert.Equal(EvaluationStatuses.Completed, rows[0].Status);
        Assert.Equal(ScoreSources.Combined, rows[0].ScoreSource);
        Assert.Contains("\"simulated\":true", rows[0].AiRawResponse);
        Assert.StartsWith("(AI giả lập)", rows[0].Reasoning);
        Assert.Equal(EvaluationStatuses.Failed, rows[1].Status);
    }

    [Fact]
    public async Task Fake_error_marker_saves_keyword_only_without_ai_score()
    {
        using var factory = TestSupport.ApiFactory(settings: Settings());
        var runId = await SeedRunAsync(factory, (FullCv + " #loi-ai", ExtractionStatuses.Completed));

        await WaitForRunAsync(factory, runId, r => r.Status == RunStatuses.Completed);

        var e = await DbAsync(factory, db => db.Evaluations.AsNoTracking().SingleAsync(x => x.RunId == runId));
        Assert.Equal(ScoreSources.KeywordOnly, e.ScoreSource);
        Assert.Null(e.AiScore);
        Assert.Contains("vượt hạn mức", e.ErrorMessage);
    }

    [Fact]
    public async Task Disabled_worker_touches_nothing()
    {
        using var factory = TestSupport.ApiFactory(settings: Settings(enabled: false));
        var runId = await SeedRunAsync(factory, (FullCv, ExtractionStatuses.Completed));

        await Task.Delay(1500);

        var run = await DbAsync(factory, db => db.EvaluationRuns.AsNoTracking().SingleAsync(r => r.RunId == runId));
        Assert.Equal((RunStatuses.Queued, 0), (run.Status, run.ProcessedItems));
    }

    [Fact]
    public void Worker_is_off_by_default_and_registered_once()
    {
        using var factory = TestSupport.ApiFactory();
        var options = factory.Services.GetRequiredService<IOptions<ScoringOptions>>().Value;

        Assert.False(options.WorkerEnabled);
        Assert.Equal(3, options.PollIntervalSeconds);
        Assert.Single(factory.Services.GetServices<IHostedService>().OfType<RunScoringWorker>());
    }

    [Fact]
    public void Invalid_poll_interval_stops_startup()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:PollIntervalSeconds"] = "0" });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Run_waiting_for_extraction_does_not_block_other_runs()
    {
        using var factory = TestSupport.ApiFactory(settings: Settings());
        var blocked = await SeedRunAsync(factory, ("", ExtractionStatuses.Pending));
        var other = await SeedRunAsync(factory, (FullCv, ExtractionStatuses.Completed));

        await WaitForRunAsync(factory, other, r => r.Status == RunStatuses.Completed);

        var run = await WaitForRunAsync(factory, blocked, r => r.Status == RunStatuses.Running);
        Assert.Equal(0, run.ProcessedItems);
        Assert.Null(run.CompletedAt);
    }

    [Fact]
    public async Task Stopped_host_resumes_in_a_new_host_without_rescoring()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var items = Enumerable.Repeat((FullCv, ExtractionStatuses.Completed), 8).ToArray();

        var first = TestSupport.ApiFactory(settings: Settings(fakeDelayMs: 150), dbName: dbName);
        var runId = await SeedRunAsync(first, items);
        await WaitForRunAsync(first, runId, r => r.ProcessedItems >= 2);
        await first.DisposeAsync();

        var stopped = TestSupport.InMemoryDb(dbName);
        var before = await stopped.Evaluations.AsNoTracking().Where(e => e.RunId == runId && e.Status == EvaluationStatuses.Completed)
            .Select(e => new { e.EvaluationId, e.Reasoning, e.FinalScore }).ToListAsync();
        var halfway = await stopped.EvaluationRuns.AsNoTracking().SingleAsync(r => r.RunId == runId);
        await stopped.DisposeAsync();
        Assert.Equal(RunStatuses.Running, halfway.Status);
        Assert.InRange(before.Count, 2, 7);
        Assert.Equal(before.Count, halfway.ProcessedItems);

        using var second = TestSupport.ApiFactory(settings: Settings(), dbName: dbName);
        var run = await WaitForRunAsync(second, runId, r => r.Status == RunStatuses.Completed);

        Assert.Equal((8, 0), (run.ProcessedItems, run.FailedItems));
        var after = await DbAsync(second, db => db.Evaluations.AsNoTracking().Where(e => e.RunId == runId).ToListAsync());
        Assert.Equal(8, after.Count);
        Assert.All(before, b => Assert.Equal(b.FinalScore, after.Single(a => a.EvaluationId == b.EvaluationId).FinalScore));
        Assert.Equal(1, await DbAsync(second, db => db.EvaluationRuns.CountAsync()));
    }

    [Fact]
    public async Task Idle_worker_polls_on_schedule_not_in_a_tight_loop()
    {
        var created = 0;
        using var factory = TestSupport.ApiFactory(settings: Settings()).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped(sp => { Interlocked.Increment(ref created); return ActivatorUtilities.CreateInstance<RunScoringProcessor>(sp); })));
        _ = factory.Services;

        await Task.Delay(3500);

        Assert.InRange(Volatile.Read(ref created), 2, 5);
    }

    private sealed class FailFirstSaves : SaveChangesInterceptor
    {
        public static int Remaining;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) =>
            eventData.Context!.ChangeTracker.Entries<EvaluationRun>().Any(e => e.State == EntityState.Modified) && Interlocked.Decrement(ref Remaining) >= 0
                ? throw new DbUpdateException("Mất kết nối database")
                : ValueTask.FromResult(result);
    }

    [Fact]
    public async Task Worker_survives_database_errors_and_finishes_later()
    {
        var dbName = Guid.NewGuid().ToString("N");
        FailFirstSaves.Remaining = 1;
        using var factory = TestSupport.ApiFactory(settings: Settings(), dbName: dbName).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<DbContextOptions<SmartCvDbContext>>();
            s.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<SmartCvDbContext>>();
            s.AddDbContext<SmartCvDbContext>(o => o.UseInMemoryDatabase(dbName).AddInterceptors(new FailFirstSaves()));
        }));
        var runId = await SeedRunAsync(factory, (FullCv, ExtractionStatuses.Completed));

        var run = await WaitForRunAsync(factory, runId, r => r.Status == RunStatuses.Completed);

        Assert.Equal((1, 0), (run.ProcessedItems, run.FailedItems));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 6)]
    [InlineData(2, 12)]
    [InlineData(5, 60)]
    [InlineData(1000, 60)]
    public void Error_backoff_is_bounded(int errors, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RunScoringWorker.NextDelay(TimeSpan.FromSeconds(3), errors));
}
