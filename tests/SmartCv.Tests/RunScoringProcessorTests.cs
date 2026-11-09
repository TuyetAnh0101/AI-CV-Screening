using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

/// <summary>A11: chấm một lượt trên database InMemory có 5 job nền, AI giả lập bằng stub, không gọi Gemini.</summary>
public partial class RunScoringProcessorTests
{
    private const int AiJob = 3;       // Python, Machine Learning, LLM API bắt buộc; không yêu cầu kinh nghiệm
    private const string GoodCv = "GPA 3.5. Python, Machine Learning, LLM API, Prompt Engineering, SQL, NLP.";
    private const string MissingMlCv = "GPA 3.5. Python, LLM API.";

    private sealed class StubAi(Func<AiEvaluationRequest, CancellationToken, Task<AiEvaluationResult>>? respond = null) : IAiEvaluator
    {
        private int _active;
        public List<AiEvaluationRequest> Calls { get; } = [];
        public bool Overlapped { get; private set; }

        public async Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _active) > 1) Overlapped = true;
            try
            {
                Calls.Add(request);
                await Task.Yield();
                return respond is null ? Success(90m, request) : await respond(request, ct);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private static AiEvaluationResult Success(decimal score, AiEvaluationRequest request) => new()
    {
        IsSuccess = true, Score = score, Reasoning = "Lý do AI.", RawResponse = "{\"score\":90}", Attempts = 1,
        MatchedSkillCodes = request.Job.Skills.Where(s => request.CvText.Contains(s.DisplayName)).Select(s => s.SkillCode).ToList()
    };

    private sealed class Env(string name) : IDisposable
    {
        public SmartCvDbContext Db { get; } = TestSupport.InMemoryDb(name);
        public string Name { get; } = name;
        public int PromptId { get; set; }
        public void Dispose() => Db.Dispose();

        public RunScoringProcessor Processor(IAiEvaluator ai, SmartCvDbContext? db = null, decimal? cap = 69m) =>
            new(db ?? Db, ai, Options.Create(new ScoringOptions { MissingMustHaveCap = cap }), NullLogger<RunScoringProcessor>.Instance);

        public SmartCvDbContext Fresh() => TestSupport.InMemoryDb(Name);
    }

    private static async Task<Env> CreateAsync()
    {
        var env = new Env(Guid.NewGuid().ToString("N"));
        await env.Db.Database.EnsureCreatedAsync();
        var prompt = new PromptVersion { VersionName = "v-lượt", SystemPrompt = "PROMPT-CUA-LUOT", IsActive = false, CreatedAt = DateTime.UtcNow };
        env.Db.PromptVersions.Add(prompt);
        env.Db.PromptVersions.Add(new PromptVersion { VersionName = "v-active", SystemPrompt = "PROMPT-DANG-KICH-HOAT", IsActive = true, CreatedAt = DateTime.UtcNow });
        await env.Db.SaveChangesAsync();
        env.PromptId = prompt.PromptVersionId;
        return env;
    }

    private static async Task<int> AddRunAsync(Env env, int total, string status = RunStatuses.Queued, DateTime? createdAt = null,
        string model = "model-luot", decimal temperature = 0.4m, decimal keywordWeight = 0.3m, decimal aiWeight = 0.7m, decimal pass = 70m)
    {
        var run = new EvaluationRun
        {
            RunType = RunTypes.Screening, PromptVersionId = env.PromptId, ModelName = model, Temperature = temperature, KeywordWeight = keywordWeight,
            AiWeight = aiWeight, PassThreshold = pass, Status = status, TotalItems = total, CreatedAt = createdAt ?? DateTime.UtcNow
        };
        env.Db.EvaluationRuns.Add(run);
        await env.Db.SaveChangesAsync();
        return run.RunId;
    }

    private static async Task<int> AddItemAsync(Env env, int runId, string? text, string extraction = ExtractionStatuses.Completed, string status = EvaluationStatuses.Pending)
    {
        var cvId = Guid.NewGuid();
        env.Db.Cvs.Add(new Cv
        {
            CvId = cvId, OriginalFileName = "cv.pdf", StoredPath = $"Uploads/CV/{cvId}.pdf", ContentType = "application/pdf", FileSizeBytes = 1,
            FileHash = cvId.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = extraction, ExtractedText = text, UploadedAt = DateTime.UtcNow
        });
        var application = new Application { CvId = cvId, JobId = AiJob, CreatedAt = DateTime.UtcNow };
        env.Db.Applications.Add(application);
        await env.Db.SaveChangesAsync();

        var evaluation = new Evaluation { RunId = runId, ApplicationId = application.ApplicationId, Status = status, CreatedAt = DateTime.UtcNow };
        env.Db.Evaluations.Add(evaluation);
        await env.Db.SaveChangesAsync();
        return evaluation.EvaluationId;
    }

    private static async Task<EvaluationRun> RunOf(Env env, int id)
    {
        await using var db = env.Fresh();
        return await db.EvaluationRuns.AsNoTracking().SingleAsync(r => r.RunId == id);
    }

    private static async Task<Evaluation> EvalOf(Env env, int id)
    {
        await using var db = env.Fresh();
        return await db.Evaluations.AsNoTracking().SingleAsync(e => e.EvaluationId == id);
    }

    private static async Task<List<EvaluationSkill>> SkillsOf(Env env, int id)
    {
        await using var db = env.Fresh();
        return await db.EvaluationSkills.AsNoTracking().Where(s => s.EvaluationId == id).ToListAsync();
    }

    // ---------- Chọn lượt ----------

    [Fact]
    public async Task Finds_only_queued_and_running_oldest_first()
    {
        using var env = await CreateAsync();
        var now = DateTime.UtcNow;
        var newest = await AddRunAsync(env, 0, RunStatuses.Queued, now);
        var oldest = await AddRunAsync(env, 0, RunStatuses.Running, now.AddHours(-2));
        var middle = await AddRunAsync(env, 0, RunStatuses.Queued, now.AddHours(-1));
        await AddRunAsync(env, 0, RunStatuses.Completed, now.AddHours(-3));
        await AddRunAsync(env, 0, RunStatuses.Failed, now.AddHours(-4));

        Assert.Equal([oldest, middle, newest], await env.Processor(new StubAi()).FindActiveRunsAsync(default));
    }

    [Theory]
    [InlineData(RunStatuses.Completed)]
    [InlineData(RunStatuses.Failed)]
    public async Task Finished_run_is_never_processed(string status)
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1, status);
        var item = await AddItemAsync(env, run, GoodCv);
        var ai = new StubAi();

        Assert.Equal(0, await env.Processor(ai).ProcessRunAsync(run, default));
        Assert.Empty(ai.Calls);
        Assert.Equal(EvaluationStatuses.Pending, (await EvalOf(env, item)).Status);
    }

    // ---------- Luồng chấm ----------

    [Fact]
    public async Task Queued_run_is_scored_saved_and_completed()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 2);
        var a = await AddItemAsync(env, run, GoodCv);
        var b = await AddItemAsync(env, run, GoodCv);

        Assert.Equal(2, await env.Processor(new StubAi()).ProcessRunAsync(run, default));

        var saved = await RunOf(env, run);
        Assert.Equal(RunStatuses.Completed, saved.Status);
        Assert.NotNull(saved.CompletedAt);
        Assert.Equal((2, 0), (saved.ProcessedItems, saved.FailedItems));
        foreach (var id in new[] { a, b })
        {
            var e = await EvalOf(env, id);
            Assert.Equal(EvaluationStatuses.Completed, e.Status);
            Assert.Equal(100m, e.KeywordScore);
            Assert.Equal(90m, e.AiScore);
            Assert.Equal(93m, e.FinalScore);   // 0.3 × 100 + 0.7 × 90
            Assert.Equal(ScoreSources.Combined, e.ScoreSource);
            Assert.Equal("Rất phù hợp", e.FitLabel);
            Assert.True(e.PredictedPass);
            Assert.Equal("{\"score\":90}", e.AiRawResponse);
            Assert.Null(e.ErrorMessage);
            Assert.Equal(6, (await SkillsOf(env, id)).Count);
        }
    }

    [Fact]
    public async Task Uses_settings_of_the_run_not_active_prompt_or_config()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1, keywordWeight: 0.5m, aiWeight: 0.5m, pass: 96m);
        var item = await AddItemAsync(env, run, GoodCv);
        var ai = new StubAi();

        await env.Processor(ai).ProcessRunAsync(run, default);

        var call = Assert.Single(ai.Calls);
        Assert.Equal(("model-luot", 0.4m, "PROMPT-CUA-LUOT", GoodCv), (call.Model, call.Temperature, call.SystemPrompt, call.CvText));
        Assert.Equal(AiJob, call.Job.JobId);
        var e = await EvalOf(env, item);
        Assert.Equal(95m, e.FinalScore);
        Assert.False(e.PredictedPass);
    }

    [Fact]
    public async Task Missing_must_have_is_capped_through_score_combiner()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        var item = await AddItemAsync(env, run, MissingMlCv);

        await env.Processor(new StubAi((r, _) => Task.FromResult(Success(100m, r)))).ProcessRunAsync(run, default);

        var e = await EvalOf(env, item);
        Assert.Equal(69m, e.FinalScore);
        Assert.Contains("giới hạn 69", e.Reasoning);
    }

    [Fact]
    public async Task Skill_rows_store_match_and_evidence_for_this_evaluation_only()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 2);
        var a = await AddItemAsync(env, run, MissingMlCv);
        var b = await AddItemAsync(env, run, GoodCv);

        await env.Processor(new StubAi()).ProcessRunAsync(run, default);

        var skills = await SkillsOf(env, a);
        Assert.Contains("Python", skills.Single(s => s.SkillCode == "python").Evidence);
        Assert.False(skills.Single(s => s.SkillCode == "machine_learning").IsMatched);
        Assert.True((await SkillsOf(env, b)).Single(s => s.SkillCode == "machine_learning").IsMatched);
    }

    [Fact]
    public async Task Existing_skill_rows_are_updated_without_duplicates()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        var item = await AddItemAsync(env, run, GoodCv);
        env.Db.EvaluationSkills.Add(new EvaluationSkill { EvaluationId = item, JobId = AiJob, SkillCode = "python", IsMatched = false, Evidence = "cũ" });
        await env.Db.SaveChangesAsync();
        env.Db.ChangeTracker.Clear();

        await env.Processor(new StubAi()).ProcessRunAsync(run, default);

        var skills = await SkillsOf(env, item);
        Assert.Equal(6, skills.Count);
        Assert.True(skills.Single(s => s.SkillCode == "python").IsMatched);
    }

    // ---------- Trạng thái CV ----------

    [Fact]
    public async Task Cv_still_extracting_stays_pending_and_run_is_not_completed()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 2);
        var done = await AddItemAsync(env, run, GoodCv);
        var waiting = await AddItemAsync(env, run, null, ExtractionStatuses.Pending);
        var ai = new StubAi();

        Assert.Equal(1, await env.Processor(ai).ProcessRunAsync(run, default));

        Assert.Single(ai.Calls);
        Assert.Equal(EvaluationStatuses.Completed, (await EvalOf(env, done)).Status);
        Assert.Equal(EvaluationStatuses.Pending, (await EvalOf(env, waiting)).Status);
        var saved = await RunOf(env, run);
        Assert.Equal(RunStatuses.Running, saved.Status);
        Assert.Null(saved.CompletedAt);
        Assert.Equal((1, 0), (saved.ProcessedItems, saved.FailedItems));
    }

    [Fact]
    public async Task Re_extracting_cv_with_old_text_is_not_scored()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        var item = await AddItemAsync(env, run, GoodCv, ExtractionStatuses.Pending);
        var ai = new StubAi();

        await env.Processor(ai).ProcessRunAsync(run, default);

        Assert.Empty(ai.Calls);
        Assert.Equal(EvaluationStatuses.Pending, (await EvalOf(env, item)).Status);
    }

    [Fact]
    public async Task Pending_cv_is_scored_after_extraction_finishes()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        var item = await AddItemAsync(env, run, null, ExtractionStatuses.Pending);
        var ai = new StubAi();
        await env.Processor(ai).ProcessRunAsync(run, default);

        var cv = await env.Db.Applications.Where(a => a.Evaluations.Any(e => e.EvaluationId == item)).Select(a => a.Cv).SingleAsync();
        cv.ExtractionStatus = ExtractionStatuses.Completed;
        cv.ExtractedText = GoodCv;
        await env.Db.SaveChangesAsync();
        env.Db.ChangeTracker.Clear();

        Assert.Equal(1, await env.Processor(ai).ProcessRunAsync(run, default));
        Assert.Equal(RunStatuses.Completed, (await RunOf(env, run)).Status);
    }

    [Fact]
    public async Task Failed_extraction_marks_failed_without_ai_or_score()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        var item = await AddItemAsync(env, run, null, ExtractionStatuses.Failed);
        var ai = new StubAi();

        await env.Processor(ai).ProcessRunAsync(run, default);

        Assert.Empty(ai.Calls);
        var e = await EvalOf(env, item);
        Assert.Equal(EvaluationStatuses.Failed, e.Status);
        Assert.Contains("ExtractionStatus = Failed", e.ErrorMessage);
        Assert.Null(e.FinalScore);
        Assert.Null(e.KeywordScore);
        var saved = await RunOf(env, run);
        Assert.Equal((1, 1, RunStatuses.Completed), (saved.ProcessedItems, saved.FailedItems, saved.Status));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   \n")]
    public async Task Empty_text_fails_with_its_own_reason_and_no_ai_call(string? text)
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        var item = await AddItemAsync(env, run, text);
        var ai = new StubAi();

        await env.Processor(ai).ProcessRunAsync(run, default);

        Assert.Empty(ai.Calls);
        var e = await EvalOf(env, item);
        Assert.Equal(EvaluationStatuses.Failed, e.Status);
        Assert.Equal("CV đã trích xuất nhưng không có nội dung chữ.", e.ErrorMessage);
    }

    // ---------- AI ----------

    [Fact]
    public async Task Ai_failure_saves_keyword_only_and_is_not_counted_failed()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        var item = await AddItemAsync(env, run, GoodCv);
        var ai = new StubAi((_, _) => Task.FromResult(AiEvaluationResult.Failure(AiErrorKind.QuotaExceeded, "Gemini vượt hạn mức sau 3 lần thử lại", 3)));

        await env.Processor(ai).ProcessRunAsync(run, default);

        var e = await EvalOf(env, item);
        Assert.Equal(EvaluationStatuses.Completed, e.Status);
        Assert.Equal(ScoreSources.KeywordOnly, e.ScoreSource);
        Assert.Null(e.AiScore);
        Assert.Equal(100m, e.FinalScore);
        Assert.Equal("Gemini vượt hạn mức sau 3 lần thử lại", e.ErrorMessage);
        Assert.Contains("điểm chỉ tính theo từ khóa", e.Reasoning);
        Assert.Single(ai.Calls);   // worker không tự thử lại
        Assert.Equal((1, 0), ((await RunOf(env, run)).ProcessedItems, (await RunOf(env, run)).FailedItems));
    }

    [Fact]
    public async Task One_cv_throwing_does_not_stop_the_others()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 3);
        var first = await AddItemAsync(env, run, GoodCv + " HONG");
        var second = await AddItemAsync(env, run, GoodCv);
        var third = await AddItemAsync(env, run, GoodCv);
        var ai = new StubAi((r, _) => r.CvText.Contains("HONG")
            ? throw new InvalidOperationException("dữ liệu riêng tư trong thông báo")
            : Task.FromResult(Success(90m, r)));

        Assert.Equal(3, await env.Processor(ai).ProcessRunAsync(run, default));

        var e = await EvalOf(env, first);
        Assert.Equal(EvaluationStatuses.Failed, e.Status);
        Assert.DoesNotContain("riêng tư", e.ErrorMessage);
        Assert.Equal(EvaluationStatuses.Completed, (await EvalOf(env, second)).Status);
        Assert.Equal(EvaluationStatuses.Completed, (await EvalOf(env, third)).Status);
        var saved = await RunOf(env, run);
        Assert.Equal((3, 1, RunStatuses.Completed), (saved.ProcessedItems, saved.FailedItems, saved.Status));
    }

    // ---------- Tiến độ ----------

    [Fact]
    public async Task Progress_is_visible_after_each_item_and_matches_saved_rows()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 3);
        for (var i = 0; i < 3; i++) await AddItemAsync(env, run, GoodCv);
        var seen = new List<int>();
        var ai = new StubAi(async (r, _) => { seen.Add((await RunOf(env, run)).ProcessedItems); return Success(90m, r); });

        await env.Processor(ai).ProcessRunAsync(run, default);

        Assert.Equal([0, 1, 2], seen);
    }

    [Fact]
    public async Task Progress_counts_are_rebuilt_from_rows_and_never_double_count()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 3, RunStatuses.Running);
        await AddItemAsync(env, run, GoodCv, status: EvaluationStatuses.Completed);
        await AddItemAsync(env, run, null, ExtractionStatuses.Failed, EvaluationStatuses.Failed);
        await AddItemAsync(env, run, GoodCv);
        var row = await env.Db.EvaluationRuns.SingleAsync(r => r.RunId == run);
        row.ProcessedItems = 99;   // số sai từ trước
        row.FailedItems = 99;
        await env.Db.SaveChangesAsync();
        env.Db.ChangeTracker.Clear();

        var processor = env.Processor(new StubAi());
        await processor.ProcessRunAsync(run, default);
        await processor.ProcessRunAsync(run, default);

        var saved = await RunOf(env, run);
        Assert.Equal((3, 1), (saved.ProcessedItems, saved.FailedItems));
    }

    [Theory]
    [InlineData(5, 13, 38.46)]
    [InlineData(13, 13, 100)]
    [InlineData(0, 13, 0)]
    [InlineData(20, 13, 100)]
    [InlineData(0, 0, 100)]
    public void Percent_stays_within_range(int processed, int total, decimal expected) =>
        Assert.Equal(expected, RunScoringProcessor.ProgressPercent(processed, total));

    [Fact]
    public async Task Run_without_items_completes()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 0);

        await env.Processor(new StubAi()).ProcessRunAsync(run, default);

        Assert.Equal(RunStatuses.Completed, (await RunOf(env, run)).Status);
    }

    [Fact]
    public async Task Run_missing_some_items_is_not_completed()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 2);
        await AddItemAsync(env, run, GoodCv);

        await env.Processor(new StubAi()).ProcessRunAsync(run, default);

        Assert.Equal(RunStatuses.Running, (await RunOf(env, run)).Status);
    }

    // ---------- Khởi động lại, hủy ----------

    [Fact]
    public async Task Completed_items_are_never_scored_again()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 2, RunStatuses.Running);
        var done = await AddItemAsync(env, run, GoodCv, status: EvaluationStatuses.Completed);
        var row = await env.Db.Evaluations.SingleAsync(e => e.EvaluationId == done);
        row.FinalScore = 12.34m;
        await env.Db.SaveChangesAsync();
        env.Db.ChangeTracker.Clear();
        await AddItemAsync(env, run, GoodCv);
        var ai = new StubAi();

        await env.Processor(ai).ProcessRunAsync(run, default);

        Assert.Single(ai.Calls);
        Assert.Equal(12.34m, (await EvalOf(env, done)).FinalScore);
    }

    [Fact]
    public async Task Shutdown_keeps_item_pending_and_restart_continues_without_duplicates()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 3);
        var ids = new List<int>();
        for (var i = 0; i < 3; i++) ids.Add(await AddItemAsync(env, run, GoodCv));

        using var cts = new CancellationTokenSource();
        var firstAi = new StubAi(async (r, ct) =>
        {
            if (Interlocked.Increment(ref _shutdownCalls) == 2)
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
            return Success(90m, r);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Processor(firstAi).ProcessRunAsync(run, cts.Token));

        Assert.Equal(EvaluationStatuses.Completed, (await EvalOf(env, ids[0])).Status);
        var interrupted = await EvalOf(env, ids[1]);
        Assert.Equal(EvaluationStatuses.Pending, interrupted.Status);
        Assert.Null(interrupted.ErrorMessage);
        Assert.Equal(EvaluationStatuses.Pending, (await EvalOf(env, ids[2])).Status);
        var halfway = await RunOf(env, run);
        Assert.Equal((RunStatuses.Running, 1, 0), (halfway.Status, halfway.ProcessedItems, halfway.FailedItems));

        // "Khởi động lại": DbContext và processor mới
        await using var restarted = env.Fresh();
        var secondAi = new StubAi();
        Assert.Equal([run], await env.Processor(secondAi, restarted).FindActiveRunsAsync(default));
        Assert.Equal(2, await env.Processor(secondAi, restarted).ProcessRunAsync(run, default));

        Assert.Equal(2, secondAi.Calls.Count);
        var final = await RunOf(env, run);
        Assert.Equal((RunStatuses.Completed, 3, 0), (final.Status, final.ProcessedItems, final.FailedItems));
        await using var check = env.Fresh();
        Assert.Equal(3, await check.Evaluations.CountAsync(e => e.RunId == run));
        Assert.Equal(18, await check.EvaluationSkills.CountAsync(s => ids.Contains(s.EvaluationId)));
    }

    private int _shutdownCalls;

    [Fact]
    public async Task Already_cancelled_token_changes_nothing()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1, RunStatuses.Running);
        var item = await AddItemAsync(env, run, GoodCv);
        var ai = new StubAi();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Processor(ai).ProcessRunAsync(run, cts.Token));

        Assert.Empty(ai.Calls);
        Assert.Equal(EvaluationStatuses.Pending, (await EvalOf(env, item)).Status);
    }

    [Fact]
    public async Task Items_are_scored_one_at_a_time()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 5);
        for (var i = 0; i < 5; i++) await AddItemAsync(env, run, GoodCv);
        var ai = new StubAi();

        await env.Processor(ai).ProcessRunAsync(run, default);

        Assert.Equal(5, ai.Calls.Count);
        Assert.False(ai.Overlapped);
    }

    // ---------- Lỗi database ----------

    private sealed class FailingSaves(int skip) : SaveChangesInterceptor
    {
        private int _count;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) =>
            Interlocked.Increment(ref _count) > skip
                ? throw new DbUpdateException("Mất kết nối database")
                : ValueTask.FromResult(result);
    }

    [Fact]
    public async Task Database_outage_does_not_mark_items_failed()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        var item = await AddItemAsync(env, run, GoodCv);
        await using var broken = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>()
            .UseInMemoryDatabase(env.Name).AddInterceptors(new FailingSaves(skip: 1)).Options);

        await Assert.ThrowsAsync<DbUpdateException>(() => env.Processor(new StubAi(), broken).ProcessRunAsync(run, default));

        var e = await EvalOf(env, item);
        Assert.Equal(EvaluationStatuses.Pending, e.Status);
        Assert.Null(e.ErrorMessage);
        Assert.Equal(0, (await RunOf(env, run)).ProcessedItems);
        Assert.Empty(await SkillsOf(env, item));
        Assert.Null(e.FinalScore);
        Assert.Null(e.AiRawResponse);
        await using var recovered = env.Fresh();
        Assert.Equal(1, await env.Processor(new StubAi(), recovered).ProcessRunAsync(run, default));
        Assert.Equal(EvaluationStatuses.Completed, (await EvalOf(env, item)).Status);
        Assert.Equal(6, (await SkillsOf(env, item)).Count);
        Assert.Equal(1, (await RunOf(env, run)).ProcessedItems);
    }

    [Fact]
    public async Task Lock_is_a_no_op_outside_sql_server()
    {
        using var env = await CreateAsync();
        await using var workerLock = await env.Processor(new StubAi()).TryAcquireWorkerLockAsync(default);
        Assert.NotNull(workerLock);
    }
}
