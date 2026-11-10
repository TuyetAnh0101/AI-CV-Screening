using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

public class PromptAccuracyPersistenceTests
{
    private sealed class NeverAi : IAiEvaluator
    {
        public int Calls { get; private set; }
        public Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("Accuracy recovery must not call AI.");
        }
    }

    private static async Task<(SmartCvDbContext Db, int RunId)> SeedAsync(string status = "Completed", string type = "PromptTest")
    {
        var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString("N"));
        await db.Database.EnsureCreatedAsync();
        var set = new EvaluationSet { Name = "Synthetic accuracy set" };
        var run = new EvaluationRun
        {
            RunType = type, Status = status, TotalItems = 3, EvaluationSet = type == "PromptTest" ? set : null,
            PromptVersion = new PromptVersion { VersionName = "selected", SystemPrompt = "saved", IsActive = false },
            ModelName = "saved-model", KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70
        };
        var jobId = await db.Jobs.Select(j => j.JobId).FirstAsync();
        for (var i = 0; i < 3; i++)
        {
            var app = new Application
            {
                JobId = jobId, Cv = new Cv
                {
                    CvId = Guid.NewGuid(), OriginalFileName = "same-name.pdf", StoredPath = "synthetic",
                    ContentType = "application/pdf", FileHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
                    ExtractionStatus = "Completed", ExtractedText = "Synthetic CV."
                }
            };
            run.Evaluations.Add(new Evaluation
            {
                Application = app, Status = i == 2 ? "Failed" : "Completed", PredictedPass = i == 2 ? null : i == 0,
                AiScore = i == 0 ? 0 : 100, FinalScore = i == 0 ? 80 : 10,
                ScoreSource = i == 0 ? "KeywordOnly" : "Combined", AiRawResponse = "saved-response"
            });
            set.HrLabels.Add(new HrLabel { Application = app, IsPass = i != 1, LabeledBy = "Synthetic HR" });
        }
        db.AddRange(set, run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (db, run.RunId);
    }

    private static RunScoringProcessor Processor(SmartCvDbContext db, NeverAi ai) =>
        new(db, ai, Options.Create(new ScoringOptions()), NullLogger<RunScoringProcessor>.Instance);

    [Fact]
    public async Task Completed_run_is_recovered_once_without_rescoring_or_modifying_saved_results()
    {
        var (db, runId) = await SeedAsync();
        await using var lifetime = db;
        var ai = new NeverAi();
        var processor = Processor(db, ai);
        Assert.Contains(runId, await processor.FindActiveRunsAsync(default));
        var before = await db.Evaluations.AsNoTracking().OrderBy(e => e.EvaluationId)
            .Select(e => new { e.Status, e.FinalScore, e.AiRawResponse, e.PredictedPass }).ToListAsync();
        Assert.Equal(0, await processor.ProcessRunAsync(runId, default));
        Assert.Equal(66.67m, (await db.EvaluationRuns.AsNoTracking().SingleAsync()).AccuracyPercent);
        Assert.DoesNotContain(runId, await processor.FindActiveRunsAsync(default));
        Assert.Equal(0, await processor.ProcessRunAsync(runId, default));
        var after = await db.Evaluations.AsNoTracking().OrderBy(e => e.EvaluationId)
            .Select(e => new { e.Status, e.FinalScore, e.AiRawResponse, e.PredictedPass }).ToListAsync();
        Assert.Equal(before, after);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task Running_run_finalizes_and_persists_accuracy_including_failed_cv()
    {
        var (db, runId) = await SeedAsync("Running");
        await using var lifetime = db;
        var ai = new NeverAi();
        await Processor(db, ai).ProcessRunAsync(runId, default);
        var run = await db.EvaluationRuns.AsNoTracking().SingleAsync();
        Assert.Equal(("Completed", 3, 1, 66.67m), (run.Status, run.ProcessedItems, run.FailedItems, run.AccuracyPercent));
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task Pending_extraction_does_not_publish_accuracy_while_running()
    {
        var (db, runId) = await SeedAsync("Running");
        await using var lifetime = db;
        var evaluation = await db.Evaluations.Include(e => e.Application.Cv).FirstAsync();
        evaluation.Status = "Pending";
        evaluation.Application.Cv.ExtractionStatus = "Pending";
        await db.SaveChangesAsync();
        var ai = new NeverAi();
        await Processor(db, ai).ProcessRunAsync(runId, default);
        var run = await db.EvaluationRuns.AsNoTracking().SingleAsync();
        Assert.Equal("Running", run.Status);
        Assert.Null(run.AccuracyPercent);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task No_remaining_intersecting_hr_labels_does_not_create_infinite_recovery_queue()
    {
        var (db, runId) = await SeedAsync();
        await using var lifetime = db;
        db.HrLabels.RemoveRange(db.HrLabels);
        await db.SaveChangesAsync();
        var ai = new NeverAi();
        var processor = Processor(db, ai);
        Assert.Empty(await processor.FindActiveRunsAsync(default));
        await processor.ProcessRunAsync(runId, default);
        Assert.Null((await db.EvaluationRuns.AsNoTracking().SingleAsync()).AccuracyPercent);
        Assert.Empty(await processor.FindActiveRunsAsync(default));
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task Screening_completion_never_calculates_prompt_accuracy()
    {
        var (db, runId) = await SeedAsync("Running", "Screening");
        await using var lifetime = db;
        var ai = new NeverAi();
        await Processor(db, ai).ProcessRunAsync(runId, default);
        var run = await db.EvaluationRuns.AsNoTracking().SingleAsync();
        Assert.Equal("Completed", run.Status);
        Assert.Null(run.AccuracyPercent);
        Assert.Equal(0, ai.Calls);
    }
}
