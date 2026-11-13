using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Privacy;
using SmartCv.Infrastructure.Storage;

namespace SmartCv.Tests;

public class A39SqlTests
{
    private const string Pii = "SYNTHETIC_PRIVATE_A39@example.invalid";
    [A39SqlFact]
    public async Task Manual_wipes_personal_content_preserves_scores_history_labels_shared_candidate_and_all_reads()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        using (var response = await env.Http.PostAsJsonAsync($"/api/cvs/{seed.CvId}/anonymize", new { actor = "HR synthetic" }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<AnonymizeResultDto>())!;
            Assert.False(result.AlreadyAnonymized);
            Assert.True(result.FileDeleted);
            Assert.False(result.CandidateDeleted);
            Assert.Equal(1, result.EvaluationsCleared);
            Assert.Equal(DateTimeKind.Utc, result.AnonymizedAt.Kind);
        }

        Assert.False(env.Files.Exists(seed.Path));
        foreach (var path in new[]
        {
            $"/api/cvs/{seed.CvId}",
            $"/api/runs/{seed.RunId}/ranking",
            $"/api/runs/{seed.RunId}",
            $"/api/evaluations/{seed.EvalIds[0]}",
            $"/api/runs/{seed.RunId}/compare?evaluationIds={string.Join(',', seed.EvalIds)}",
            $"/api/evaluation-sets/{seed.SetId}",
            $"/api/evaluation-sets/{seed.SetId}/applications",
            $"/api/evaluation-sets/{seed.SetId}/label-suggestions",
            $"/api/evaluation-sets/{seed.SetId}/labels/template",
            $"/api/prompt-tests/{seed.RunId}"}

        )
        {
            using var response = await env.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // The other CV deliberately uses another sentinel/name, not the erased CV's marker.
            Assert.DoesNotContain(Pii, await response.Content.ReadAsStringAsync());
        }

        foreach (var suffix in new[]
        {
            "file",
            "text"
        }

        )
            Assert.Equal(HttpStatusCode.Gone, (await env.Http.GetAsync($"/api/cvs/{seed.CvId}/{suffix}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await env.Http.PostAsync($"/api/cvs/{seed.CvId}/extract", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await env.Http.PostAsync($"/api/applications/{seed.AppIds[0]}/ai-preview", null)).StatusCode);
        using var repeated = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.True((await repeated.Content.ReadFromJsonAsync<AnonymizeResultDto>())!.AlreadyAnonymized);
        await using var db = env.Db();
        var cv = await db.Cvs.SingleAsync(c => c.CvId == seed.CvId);
        Assert.Null(cv.CandidateId);
        Assert.Null(cv.ExtractedText);
        Assert.Null(cv.ExtractionError);
        Assert.Equal(RetentionRulesName, cv.OriginalFileName);
        Assert.StartsWith("ANON", cv.FileHash);
        Assert.Equal("Email", cv.SourceChannel);
        Assert.NotNull(cv.ConsentConfirmedAt);
        Assert.NotNull(cv.RetainUntil);
        Assert.Equal(1, await db.Candidates.CountAsync()); // retained for other CV
        var evaluation = await db.Evaluations.SingleAsync(e => e.EvaluationId == seed.EvalIds[0]);
        Assert.Equal(80m, evaluation.FinalScore);
        Assert.Null(evaluation.Reasoning);
        Assert.Null(evaluation.AiRawResponse);
        Assert.Null((await db.EvaluationSkills.SingleAsync()).Evidence);
        Assert.Equal(2, await db.HrLabels.CountAsync());
        Assert.Equal("HR note retained", (await db.HrLabels.OrderBy(l => l.ApplicationId).FirstAsync()).Note);
        Assert.Equal(2, await db.ApplicationEvents.CountAsync());
        Assert.Equal(seed.Versions, await db.Applications.OrderBy(a => a.ApplicationId).Select(a => a.StageVersion).ToArrayAsync());
        Assert.Equal(50m, (await db.EvaluationRuns.SingleAsync()).AccuracyPercent);
    }

    private const string RetentionRulesName = "Hồ sơ đã ẩn danh";
    [A39SqlTheory]
    [InlineData("Shortlisted")]
    [InlineData("Interview")]
    public async Task Any_other_job_in_manual_blocking_stage_prevents_file_deletion(string stage)
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        await using (var db = env.Db())
        {
            var app = await db.Applications.SingleAsync(a => a.ApplicationId == seed.AppIds[1]);
            app.Stage = stage;
            app.RejectReason = null;
            await db.SaveChangesAsync();
        }

        using var response = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(env.Files.Exists(seed.Path));
        await using var check = env.Db();
        Assert.Null((await check.Cvs.SingleAsync(c => c.CvId == seed.CvId)).AnonymizedAt);
    }

    [A39SqlFact]
    public async Task Two_concurrent_requests_have_one_erasure_and_one_idempotent_result()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        var responses = await Task.WhenAll(env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null), env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null));
        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var results = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<AnonymizeResultDto>()));
            Assert.Single(results, r => r!.AlreadyAnonymized);
            Assert.Single(results, r => !r!.AlreadyAnonymized);
            Assert.Equal(results[0]!.AnonymizedAt, results[1]!.AnonymizedAt);
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [A39SqlFact]
    public async Task Dry_run_future_is_read_only_and_stale_due_candidate_is_rechecked_for_active_job_and_new_deadline()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        using var preview = await env.Http.PostAsync("/api/retention/run?dryRun=true&asOf=2999-01-01", null);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var due = (await preview.Content.ReadFromJsonAsync<RetentionRunResultDto>())!;
        Assert.Contains(seed.CvId, due.CvIds);
        Assert.Equal(0, due.AnonymizedCount);
        Assert.True(env.Files.Exists(seed.Path));
        await using (var db = env.Db())
        {
            var app = await db.Applications.FindAsync(seed.AppIds[1]);
            app!.Stage = "Reviewing";
            app.RejectReason = null;
            await db.SaveChangesAsync();
        }

        using (var scope = env.Factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CvAnonymizer>().AnonymizeAsync(seed.CvId, "system", true, DateTime.UtcNow, default);
            Assert.Equal(AnonymizeOutcome.Blocked, result.Outcome);
        }

        await using (var db = env.Db())
        {
            var app = await db.Applications.FindAsync(seed.AppIds[1]);
            app!.Stage = "OnHold";
            (await db.Cvs.FindAsync(seed.CvId))!.RetainUntil = DateTime.UtcNow.AddYears(1);
            await db.SaveChangesAsync();
        }

        using (var scope = env.Factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CvAnonymizer>().AnonymizeAsync(seed.CvId, "system", true, DateTime.UtcNow, default);
            Assert.Equal(AnonymizeOutcome.Blocked, result.Outcome);
        }

        Assert.True(env.Files.Exists(seed.Path));
    }

    [A39SqlFact]
    public async Task Real_run_honors_batch_and_repeated_run_processes_remaining_candidate()
    {
        await using var env = await Env.Create(batch: 1);
        var seed = await env.Seed();
        using var first = await env.Http.PostAsync("/api/retention/run?dryRun=false", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var result = (await first.Content.ReadFromJsonAsync<RetentionRunResultDto>())!;
        Assert.Equal(1, result.DueCount);
        Assert.Equal(1, result.AnonymizedCount);
        Assert.Equal(0, result.FailedCount);
        using var second = await env.Http.PostAsync("/api/retention/run?dryRun=false", null);
        Assert.Equal(1, (await second.Content.ReadFromJsonAsync<RetentionRunResultDto>())!.AnonymizedCount);
        await using var db = env.Db();
        Assert.Equal(0, await db.Candidates.CountAsync());
        Assert.Equal(2, await db.Cvs.CountAsync(c => c.AnonymizedAt != null));
    }

    [A39SqlFact]
    public async Task Failed_SQL_commit_restores_original_file_and_all_database_content()
    {
        var fault = new CommitFault();
        await using var env = await Env.Create(fault);
        var seed = await env.Seed();
        fault.Armed = true;
        using var response = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(fault.Triggered);
        Assert.True(env.Files.Exists(seed.Path));
        await using var db = env.Db();
        var cv = await db.Cvs.SingleAsync(c => c.CvId == seed.CvId);
        Assert.Null(cv.AnonymizedAt);
        Assert.Contains(Pii, cv.ExtractedText);
        Assert.Contains(Pii, (await db.Evaluations.SingleAsync(e => e.EvaluationId == seed.EvalIds[0])).Reasoning);
        using var retry = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [A39SqlFact]
    public async Task Missing_CV_returns_404()
    {
        await using var env = await Env.Create();
        Assert.Equal(HttpStatusCode.NotFound, (await env.Http.PostAsync($"/api/cvs/{Guid.NewGuid()}/anonymize", null)).StatusCode);
    }

    [A39SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Storage_failure_before_or_after_file_delete_is_sanitized_and_restores_the_original(bool afterDelete)
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        env.Adapter.FailId = seed.CvId;
        env.Adapter.FailBefore = !afterDelete;
        env.Adapter.FailAfter = afterDelete;
        using var response = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(Pii, await response.Content.ReadAsStringAsync());
        Assert.True(env.Files.Exists(seed.Path));
        await using var db = env.Db();
        Assert.Null((await db.Cvs.FindAsync(seed.CvId))!.AnonymizedAt);
    }

    [A39SqlFact]
    public async Task Batch_failure_is_per_CV_and_does_not_erase_a_failed_candidate_or_prevent_other_candidate()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        env.Adapter.FailId = seed.CvId;
        env.Adapter.FailBefore = true;
        using var response = await env.Http.PostAsync("/api/retention/run?dryRun=false", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<RetentionRunResultDto>())!;
        Assert.Equal(2, result.DueCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.AnonymizedCount);
        Assert.DoesNotContain(seed.CvId, result.CvIds);
        Assert.True(env.Files.Exists(seed.Path));
        await using var db = env.Db();
        Assert.Null((await db.Cvs.FindAsync(seed.CvId))!.AnonymizedAt);
    }

    [A39SqlFact]
    public async Task Cleanup_failure_after_commit_reports_failure_and_repeat_purges_retained_recovery_without_rescoring()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        env.Adapter.FailId = seed.CvId;
        env.Adapter.FailComplete = true;
        using var first = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        Assert.False(env.Files.Exists(seed.Path));
        await using (var db = env.Db())
            Assert.NotNull((await db.Cvs.FindAsync(seed.CvId))!.AnonymizedAt);
        using var retry = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True((await retry.Content.ReadFromJsonAsync<AnonymizeResultDto>())!.AlreadyAnonymized);
    }

    [A39SqlTheory]
    [InlineData("Pending")]
    [InlineData("Queued")]
    [InlineData("Running")]
    public async Task Pending_OCR_or_queued_running_AI_prevents_erasure(string state)
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        await using (var db = env.Db())
        {
            if (state == "Pending")
                (await db.Cvs.FindAsync(seed.CvId))!.ExtractionStatus = state;
            else
                (await db.EvaluationRuns.SingleAsync()).Status = state;
            await db.SaveChangesAsync();
        }

        using var response = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(env.Files.Exists(seed.Path));
    }

    [A39SqlFact]
    public async Task Tombstone_guards_hide_stale_legacy_PII_even_if_storage_and_columns_still_contain_the_marker()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        await using (var db = env.Db())
        {
            (await db.Cvs.FindAsync(seed.CvId))!.AnonymizedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        foreach (var path in new[]
        {
            $"/api/cvs/{seed.CvId}",
            $"/api/applications?jobId=1",
            $"/api/runs/{seed.RunId}/ranking",
            $"/api/runs/{seed.RunId}",
            $"/api/evaluations/{seed.EvalIds[0]}",
            $"/api/runs/{seed.RunId}/compare?evaluationIds={string.Join(',', seed.EvalIds)}",
            $"/api/prompt-tests/{seed.RunId}",
            $"/api/evaluation-sets/{seed.SetId}",
            $"/api/evaluation-sets/{seed.SetId}/labels/template"}

        )
        {
            using var response = await env.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(Pii, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.Conflict, (await env.Http.GetAsync($"/api/applications/{seed.AppIds[0]}/keyword-score")).StatusCode);
    }

    [A39SqlFact]
    public async Task Interrupted_precommit_erasure_recovers_file_even_if_HR_now_blocks_erasure()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        Assert.True(env.Files.PrepareErasure(seed.CvId, seed.Path));
        Assert.False(env.Files.Exists(seed.Path));
        await using (var db = env.Db())
        {
            var app = await db.Applications.FindAsync(seed.AppIds[1]);
            app!.Stage = "Interview";
            await db.SaveChangesAsync();
        }

        using var retry = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.True(env.Files.Exists(seed.Path));
        await using var check = env.Db();
        Assert.Null((await check.Cvs.FindAsync(seed.CvId))!.AnonymizedAt);
    }

    [A39SqlFact]
    public async Task Prompt_comparison_and_two_saved_prompt_runs_keep_accuracy_without_disclosing_erased_content()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        int secondId;
        await using (var db = env.Db())
        {
            var evaluations = await db.Evaluations.AsNoTracking().Where(e => e.RunId == seed.RunId).OrderBy(e => e.EvaluationId).ToListAsync();
            var second = new EvaluationRun
            {
                RunType = "PromptTest",
                EvaluationSetId = seed.SetId,
                PromptVersion = new PromptVersion
                {
                    VersionName = "Second synthetic prompt",
                    SystemPrompt = "fake"
                },
                ModelName = "fake",
                Temperature = .2m,
                KeywordWeight = .3m,
                AiWeight = .7m,
                PassThreshold = 70,
                Status = "Completed",
                TotalItems = 2,
                ProcessedItems = 2,
                AccuracyPercent = 50m,
                CompletedAt = DateTime.UtcNow,
                Evaluations = evaluations.Select(e => new Evaluation { ApplicationId = e.ApplicationId, Status = "Completed", KeywordScore = e.KeywordScore, AiScore = e.AiScore, FinalScore = e.FinalScore, PredictedPass = e.PredictedPass, ScoreSource = e.ScoreSource, FitLabel = e.FitLabel, Reasoning = e.Reasoning, AiRawResponse = e.AiRawResponse }).ToList()
            };
            db.Add(second);
            await db.SaveChangesAsync();
            secondId = second.RunId;
        }

        using var erased = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.OK, erased.StatusCode);
        using var compared = await env.Http.GetAsync($"/api/prompt-tests/compare?baseRunId={seed.RunId}&candidateRunId={secondId}");
        Assert.Equal(HttpStatusCode.OK, compared.StatusCode);
        Assert.DoesNotContain(Pii, await compared.Content.ReadAsStringAsync());
        await using var check = env.Db();
        Assert.All(await check.EvaluationRuns.ToListAsync(), r => Assert.Equal(50m, r.AccuracyPercent));
        Assert.All(await check.Evaluations.Where(e => e.ApplicationId == seed.AppIds[0]).ToListAsync(), e =>
        {
            Assert.Equal(80m, e.FinalScore);
            Assert.Null(e.Reasoning);
            Assert.Null(e.AiRawResponse);
        });
    }

    [A39SqlFact]
    public async Task Privacy_lease_prevents_erasure_overtaking_a_read_and_hides_errors_while_exclusive_work_is_running()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        await using var connection = env.Db();
        await using (var read = await CvPrivacyScope.BeginAsync(connection, false, default))
        {
            using var blocked = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.True(env.Files.Exists(seed.Path));
        }

        await using (var erasure = await CvPrivacyScope.BeginAsync(connection, true, default))
        {
            using var blocked = await env.Http.GetAsync($"/api/evaluations/{seed.EvalIds[0]}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            Assert.DoesNotContain(Pii, await blocked.Content.ReadAsStringAsync());
        }

        using var success = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        using var detail = await env.Http.GetAsync($"/api/cvs/{seed.CvId}");
        Assert.Equal("no-store", detail.Headers.CacheControl!.ToString());
        Assert.DoesNotContain(Pii, await detail.Content.ReadAsStringAsync());
    }

    [A39SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_reconciles_interrupted_journals_even_when_CV_is_active_or_already_anonymized(bool committed)
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        if (committed)
        {
            env.Adapter.FailId = seed.CvId;
            env.Adapter.FailComplete = true;
            using var failed = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        else
        {
            Assert.True(env.Files.PrepareErasure(seed.CvId, seed.Path));
            await using var db = env.Db();
            (await db.Applications.FindAsync(seed.AppIds[1]))!.Stage = "Reviewing";
            await db.SaveChangesAsync();
        }

        using (var preview = await env.Http.PostAsync("/api/retention/run?dryRun=true", null))
        {
            Assert.Equal(1, (await preview.Content.ReadFromJsonAsync<RetentionRunResultDto>())!.RecoveryCount);
            Assert.False(env.Files.Exists(seed.Path));
            Assert.Contains(seed.CvId, env.Files.PendingErasureIds(100));
        }

        using var run = await env.Http.PostAsync("/api/retention/run?dryRun=false", null);
        var result = (await run.Content.ReadFromJsonAsync<RetentionRunResultDto>())!;
        Assert.Equal(1, result.RecoveryCount);
        Assert.Equal(1, result.RecoveredCount);
        Assert.Equal(0, result.FailedCount);
        Assert.DoesNotContain(seed.CvId, env.Files.PendingErasureIds(100));
        Assert.Equal(!committed, env.Files.Exists(seed.Path));
        await using var check = env.Db();
        Assert.Equal(committed, (await check.Cvs.FindAsync(seed.CvId))!.AnonymizedAt is not null);
    }

    [A39SqlFact]
    public async Task Anonymized_hash_releases_the_old_fingerprint_so_same_bytes_can_be_uploaded_as_a_new_CV()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        MultipartFormDataContent Upload()
        {
            var form = A38TestData.WithConsent(new MultipartFormDataContent());
            form.Add(new ByteArrayContent(TestSupport.Pdf("A39-demo")), "files", "synthetic-reupload.pdf");
            return form;
        }

        using (var form = Upload())
        using (var duplicate = await env.Http.PostAsync("/api/cvs/upload", form))
        {
            var body = (await duplicate.Content.ReadFromJsonAsync<CvUploadResponse>())!;
            Assert.Equal(0, body.SuccessCount);
            Assert.Equal("Duplicate", Assert.Single(body.Files).ErrorCode);
        }

        using var erased = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.OK, erased.StatusCode);
        using var fresh = Upload();
        using var uploaded = await env.Http.PostAsync("/api/cvs/upload", fresh);
        var result = (await uploaded.Content.ReadFromJsonAsync<CvUploadResponse>())!;
        Assert.Equal(1, result.SuccessCount);
        Assert.NotEqual(seed.CvId, Assert.Single(result.Files).CvId);
        await using var db = env.Db();
        Assert.NotNull((await db.Cvs.FindAsync(seed.CvId))!.AnonymizedAt);
    }

    [A39SqlFact]
    public async Task Cancelled_privacy_lock_acquisition_does_not_leave_a_pooled_session_blocking_future_erasure()
    {
        await using var env = await Env.Create();
        var seed = await env.Seed();
        await using var reader = env.Db();
        await using (var held = await CvPrivacyScope.BeginAsync(reader, false, default))
        {
            var sql = new SqlConnectionStringBuilder(reader.Database.GetConnectionString())
            {
                Pooling = true
            };
            await using var waiting = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(sql.ConnectionString).Options);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CvPrivacyScope.BeginAsync(waiting, true, cancel.Token));
            Assert.True(env.Files.Exists(seed.Path));
        }

        using var result = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [A39SqlFact]
    public async Task Failure_after_SQL_update_rolls_back_content_before_any_file_deletion()
    {
        var fault = new WriteFault();
        await using var env = await Env.Create(fault);
        var seed = await env.Seed();
        fault.Armed = true;
        using var response = await env.Http.PostAsync($"/api/cvs/{seed.CvId}/anonymize", null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(fault.Triggered);
        Assert.True(env.Files.Exists(seed.Path));
        await using var db = env.Db();
        var cv = await db.Cvs.FindAsync(seed.CvId);
        Assert.Null(cv!.AnonymizedAt);
        Assert.Contains(Pii, cv.ExtractedText);
        Assert.Contains(Pii, (await db.Evaluations.SingleAsync(e => e.EvaluationId == seed.EvalIds[0])).Reasoning);
    }

    private sealed class WriteFault : DbCommandInterceptor
    {
        public bool Armed, Triggered;
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
        {
            if (Armed && command.CommandText.Contains("UPDATE") && command.CommandText.Contains("[AnonymizedAt]"))
            {
                Armed = false;
                Triggered = true;
                throw new InvalidOperationException("Synthetic failure after CV SQL update");
            }

            return base.NonQueryExecutedAsync(command, eventData, result, ct);
        }
    }

    private sealed class FaultStorage(LocalCvFileStorage inner) : ICvFileStorage, ICvErasureStorage
    {
        public Guid FailId;
        public bool FailBefore, FailAfter, FailComplete;
        public Task<string> SaveAsync(Guid id, string extension, Stream content, DateTime uploadedAtUtc, CancellationToken ct = default) => inner.SaveAsync(id, extension, content, uploadedAtUtc, ct);
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public bool Exists(string path) => inner.Exists(path);
        public void Delete(string path) => inner.Delete(path);
        public IReadOnlyList<Guid> PendingErasureIds(int limit) => inner.PendingErasureIds(limit);
        public bool PrepareErasure(Guid id, string path)
        {
            if (id == FailId && FailBefore)
                throw new IOException(Pii);
            var result = inner.PrepareErasure(id, path);
            if (id == FailId && FailAfter)
                throw new IOException(Pii);
            return result;
        }

        public void RollbackErasure(Guid id) => inner.RollbackErasure(id);
        public void CompleteErasure(Guid id)
        {
            if (id == FailId && FailComplete)
            {
                FailComplete = false;
                throw new IOException(Pii);
            }

            inner.CompleteErasure(id);
        }
    }

    private sealed class CommitFault : DbTransactionInterceptor
    {
        public bool Armed;
        public bool Triggered;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        {
            if (Armed)
            {
                Armed = false;
                Triggered = true;
                throw new InvalidOperationException("Synthetic commit failure");
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, ct);
        }
    }

    private sealed record Seeded(Guid CvId, string Path, int[] AppIds, int[] EvalIds, int RunId, int SetId, Guid[] Versions);
    private sealed class Env : IAsyncDisposable
    {
        private readonly string connection;
        public WebApplicationFactory<Program> Factory = null!;
        public HttpClient Http = null!;
        public LocalCvFileStorage Files = null!;
        public FaultStorage Adapter = null!;
        private Env(string value) => connection = value;
        public SmartCvDbContext Db() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);
        public static async Task<Env> Create(IInterceptor? fault = null, int batch = 100)
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A39SqlFactAttribute.Setting));
            A39SqlFactAttribute.Validate(builder);
            builder.InitialCatalog = "SmartCv_A39_Test_" + Guid.NewGuid().ToString("N");
            builder.Pooling = false;
            builder.ConnectTimeout = 10;
            var env = new Env(builder.ConnectionString);
            var evidence = TestSupport.RepoFile("artifacts/a39-tests/retained-sql-databases.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            await File.AppendAllTextAsync(evidence, builder.InitialCatalog + Environment.NewLine);
            await using (var db = env.Db())
                await db.Database.MigrateAsync();
            env.Files = TestSupport.Storage(TestSupport.TempRoot());
            env.Adapter = new FaultStorage(env.Files);
            env.Factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false", ["Retention:BatchSize"] = batch.ToString() }).WithWebHostBuilder(b =>
            {
                b.ConfigureLogging(l => l.ClearProviders());
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                    s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                    s.AddDbContext<SmartCvDbContext>(o =>
                    {
                        o.UseSqlServer(env.connection);
                        if (fault is not null)
                            o.AddInterceptors(fault);
                    });
                    s.RemoveAll<ICvFileStorage>();
                    s.AddSingleton<ICvFileStorage>(env.Adapter);
                });
            });
            env.Http = env.Factory.CreateClient();
            return env;
        }

        public async Task<Seeded> Seed()
        {
            await using var db = Db();
            var id = Guid.NewGuid();
            using var data = new MemoryStream(TestSupport.Pdf("A39-demo"));
            var path = await Files.SaveAsync(id, ".pdf", data, DateTime.UtcNow);
            var candidate = new Candidate
            {
                FullName = "Shared candidate retained for other CV",
                Email = "other@example.invalid"
            };
            var cv = new Cv
            {
                CvId = id,
                Candidate = candidate,
                OriginalFileName = Pii + ".pdf",
                StoredPath = path,
                ContentType = "application/pdf",
                FileSizeBytes = 50,
                FileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestSupport.Pdf("A39-demo"))),
                ExtractedText = Pii,
                ExtractionStatus = "Completed",
                SourceChannel = "Email",
                ConsentConfirmedAt = DateTime.UtcNow,
                RetainUntil = DateTime.UtcNow.AddDays(-2)
            };
            var other = new Cv
            {
                CvId = Guid.NewGuid(),
                Candidate = candidate,
                OriginalFileName = "other-synthetic.pdf",
                StoredPath = "Uploads/CV/missing-other.pdf",
                ContentType = "application/pdf",
                FileSizeBytes = 1,
                FileHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
                ExtractedText = "Other synthetic CV",
                ExtractionStatus = "Completed",
                RetainUntil = DateTime.UtcNow.AddDays(-1)
            };
            var apps = new[]
            {
                new Application
                {
                    Cv = cv,
                    JobId = 1,
                    Stage = "Rejected",
                    RejectReason = "MissingSkills"
                },
                new Application
                {
                    Cv = cv,
                    JobId = 2,
                    Stage = "OnHold"
                },
                new Application
                {
                    Cv = other,
                    JobId = 1,
                    Stage = "Rejected",
                    RejectReason = "MissingSkills"
                }
            };
            db.AddRange(apps);
            await db.SaveChangesAsync();
            var set = new EvaluationSet
            {
                Name = "A39 synthetic labels"
            };
            db.Add(set);
            await db.SaveChangesAsync();
            db.HrLabels.AddRange(new HrLabel { EvaluationSetId = set.EvaluationSetId, ApplicationId = apps[0].ApplicationId, IsPass = true, Note = "HR note retained", LabeledBy = "HR" }, new HrLabel { EvaluationSetId = set.EvaluationSetId, ApplicationId = apps[2].ApplicationId, IsPass = false, Note = "Other note", LabeledBy = "HR" });
            db.ApplicationEvents.AddRange(apps.Where(a => a.Stage == "Rejected").Select(a => new ApplicationEvent { ApplicationId = a.ApplicationId, FromStage = "New", ToStage = "Rejected", RejectReason = "MissingSkills", Actor = "HR", Note = "HR history retained" }));
            var run = new EvaluationRun
            {
                RunType = "PromptTest",
                EvaluationSetId = set.EvaluationSetId,
                PromptVersion = new PromptVersion
                {
                    VersionName = "A39 synthetic",
                    SystemPrompt = "fake",
                    IsActive = true
                },
                ModelName = "fake",
                Temperature = .2m,
                KeywordWeight = .3m,
                AiWeight = .7m,
                PassThreshold = 70,
                Status = "Completed",
                TotalItems = 2,
                ProcessedItems = 2,
                AccuracyPercent = 50m,
                CompletedAt = DateTime.UtcNow,
                Evaluations = new List<Evaluation>
                {
                    new Evaluation
                    {
                        Application = apps[0],
                        Status = "Completed",
                        KeywordScore = 80,
                        AiScore = 80,
                        FinalScore = 80,
                        PredictedPass = true,
                        ScoreSource = "Combined",
                        FitLabel = "Good",
                        Reasoning = Pii,
                        AiRawResponse = "{\"private\":\"" + Pii + "\"}",
                        ErrorMessage = Pii
                    },
                    new Evaluation
                    {
                        Application = apps[2],
                        Status = "Completed",
                        KeywordScore = 75,
                        AiScore = 75,
                        FinalScore = 75,
                        PredictedPass = true,
                        ScoreSource = "Combined",
                        FitLabel = "Good",
                        Reasoning = "Other reasoning"
                    }
                }
            };
            db.Add(run);
            await db.SaveChangesAsync();
            var skill = await db.JobSkills.FirstAsync(s => s.JobId == 1);
            db.EvaluationSkills.Add(new() { EvaluationId = run.Evaluations.First().EvaluationId, JobId = 1, SkillCode = skill.SkillCode, IsMatched = true, Evidence = Pii });
            await db.SaveChangesAsync();
            return new(id, path, apps.Select(a => a.ApplicationId).ToArray(), run.Evaluations.Select(e => e.EvaluationId).ToArray(), run.RunId, set.EvaluationSetId, apps.Select(a => a.StageVersion).ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (Factory is not null)
                await Factory.DisposeAsync();
        } // retain catalog for inspection
    }
}

public sealed class A39SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A39_TEST_SQL_CONNECTION";
    public A39SqlFactAttribute()
    {
        if (!Enabled())
            Skip = $"Set {Setting} for a new retained synthetic A39 SQL database.";
    }

    internal static bool Enabled()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value))
            return false;
        Validate(new SqlConnectionStringBuilder(value));
        return true;
    }

    internal static void Validate(SqlConnectionStringBuilder value)
    {
        if (!value.InitialCatalog.StartsWith("SmartCv_A39_Test_", StringComparison.Ordinal) || !string.IsNullOrEmpty(value.AttachDBFilename))
            throw new ArgumentException("Only SmartCv_A39_Test_ without attached files is allowed.");
    }
}

public sealed class A39SqlTheoryAttribute : TheoryAttribute
{
    public A39SqlTheoryAttribute()
    {
        if (!A39SqlFactAttribute.Enabled())
            Skip = $"Set {A39SqlFactAttribute.Setting} for isolated SQL tests.";
    }
}
