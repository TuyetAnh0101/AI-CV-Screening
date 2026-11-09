using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Entities;
using SmartCv.Core.Constants;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

public partial class RunScoringProcessorTests
{
    [Fact]
    public async Task A12_failed_extraction_clears_stale_pending_results_and_skills_without_calling_ai()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        var item = await AddItemAsync(env, run, null, ExtractionStatuses.Failed);
        var stale = await env.Db.Evaluations.SingleAsync(e => e.EvaluationId == item);
        stale.KeywordScore = 90m;
        stale.AiScore = 90m;
        stale.FinalScore = 90m;
        stale.Reasoning = "stale";
        stale.AiRawResponse = "stale";
        env.Db.EvaluationSkills.Add(new EvaluationSkill { EvaluationId = item, JobId = AiJob, SkillCode = "python" });
        await env.Db.SaveChangesAsync();
        var ai = new StubAi();
        await env.Processor(ai).ProcessRunAsync(run, default);
        var saved = await EvalOf(env, item);
        Assert.Equal(EvaluationStatuses.Failed, saved.Status);
        Assert.Null(saved.KeywordScore);
        Assert.Null(saved.AiScore);
        Assert.Null(saved.FinalScore);
        Assert.Null(saved.Reasoning);
        Assert.Null(saved.AiRawResponse);
        Assert.NotEmpty(saved.ErrorMessage!);
        Assert.Empty(await SkillsOf(env, item));
        Assert.Empty(ai.Calls);
    }

    [Fact]
    public async Task A12_prompt_reference_does_not_protect_against_direct_content_edits()
    {
        // Documents the current boundary: no prompt update API exists, but direct EF/SQL
        // writes are not protected. Do not claim an independent content snapshot.
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        await AddItemAsync(env, run, GoodCv);
        await using (var edit = env.Fresh())
        {
            (await edit.PromptVersions.SingleAsync(p => p.PromptVersionId == env.PromptId)).SystemPrompt = "EDITED-DIRECTLY";
            await edit.SaveChangesAsync();
        }
        var ai = new StubAi();
        await env.Processor(ai).ProcessRunAsync(run, default);
        Assert.Equal("EDITED-DIRECTLY", Assert.Single(ai.Calls).SystemPrompt);
    }

    [Fact]
    public async Task A12_restart_after_prompt_and_defaults_change_preserves_saved_result_and_run_snapshot()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 2);
        var completed = await AddItemAsync(env, run, GoodCv);
        var pending = await AddItemAsync(env, run, GoodCv, ExtractionStatuses.Pending);
        await env.Processor(new StubAi()).ProcessRunAsync(run, default);
        var before = await EvalOf(env, completed);
        var beforeSkills = await SkillsOf(env, completed);

        await using (var edit = env.Fresh())
        {
            foreach (var prompt in await edit.PromptVersions.ToListAsync()) prompt.IsActive = false;
            edit.PromptVersions.Add(new PromptVersion { VersionName = "v-next", SystemPrompt = "NEXT", IsActive = true });
            var cv = await edit.Evaluations.Where(e => e.EvaluationId == pending).Select(e => e.Application.Cv).SingleAsync();
            cv.ExtractionStatus = ExtractionStatuses.Completed;
            await edit.SaveChangesAsync();
        }

        await using var restart = env.Fresh();
        var ai = new StubAi();
        var changed = new ScoringOptions { ModelName = "different", Temperature = 0.9m,
            KeywordWeight = 0.9m, AiWeight = 0.1m, PassThreshold = 99m, MissingMustHaveCap = 69m };
        var processor = new RunScoringProcessor(restart, ai, Options.Create(changed), NullLogger<RunScoringProcessor>.Instance);
        Assert.Equal(1, await processor.ProcessRunAsync(run, default));
        var call = Assert.Single(ai.Calls);
        Assert.Equal(("model-luot", 0.4m, "PROMPT-CUA-LUOT"), (call.Model, call.Temperature, call.SystemPrompt));
        var after = await EvalOf(env, completed);
        Assert.Equal((before.FinalScore, before.Reasoning, before.AiRawResponse), (after.FinalScore, after.Reasoning, after.AiRawResponse));
        Assert.Equal(beforeSkills.Select(s => (s.SkillCode, s.Evidence)), (await SkillsOf(env, completed)).Select(s => (s.SkillCode, s.Evidence)));
        var saved = await EvalOf(env, pending);
        Assert.Equal(93m, saved.FinalScore);
        Assert.True(saved.PredictedPass);
        Assert.Equal(2, (await RunOf(env, run)).ProcessedItems);
    }

    [Fact]
    public async Task A12_stale_skill_from_another_job_is_replaced_without_touching_other_evaluations()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 2);
        var item = await AddItemAsync(env, run, GoodCv);
        var untouched = await AddItemAsync(env, run, GoodCv, status: EvaluationStatuses.Completed);
        env.Db.EvaluationSkills.AddRange(
            new EvaluationSkill { EvaluationId = item, JobId = 999, SkillCode = "python", Evidence = "stale" },
            new EvaluationSkill { EvaluationId = untouched, JobId = AiJob, SkillCode = "python", Evidence = "keep" });
        await env.Db.SaveChangesAsync();
        await env.Processor(new StubAi()).ProcessRunAsync(run, default);
        var skills = await SkillsOf(env, item);
        Assert.Equal(6, skills.Count);
        Assert.All(skills, s => Assert.Equal(AiJob, s.JobId));
        Assert.Equal(6, skills.Select(s => s.SkillCode).Distinct().Count());
        Assert.Equal("keep", Assert.Single(await SkillsOf(env, untouched)).Evidence);
    }

    [Fact]
    public async Task A12_live_cap_is_not_snapshotted_and_can_change_pending_result()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 2);
        var first = await AddItemAsync(env, run, MissingMlCv);
        var second = await AddItemAsync(env, run, MissingMlCv, ExtractionStatuses.Pending);
        await env.Processor(new StubAi(), cap: 40m).ProcessRunAsync(run, default);
        await using var next = env.Fresh();
        var cv = await next.Evaluations.Where(e => e.EvaluationId == second).Select(e => e.Application.Cv).SingleAsync();
        cv.ExtractionStatus = ExtractionStatuses.Completed;
        await next.SaveChangesAsync();
        await env.Processor(new StubAi(), next, cap: 60m).ProcessRunAsync(run, default);
        Assert.Equal(40m, (await EvalOf(env, first)).FinalScore);
        Assert.Equal(60m, (await EvalOf(env, second)).FinalScore);
    }
}
