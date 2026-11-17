using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests.Jobs;

public class JobSkillSqlServerTests
{
    [Theory]
    [InlineData("Server=.;Database=SmartCvDb;Integrated Security=True")]
    [InlineData("Server=.;Database=SmartCv_A21_Test_abc;Integrated Security=True")]
    [InlineData("Server=.;Database=SmartCv_A22_Test_abc;AttachDbFilename=C:\\unsafe.mdf;Integrated Security=True")]
    public void SQL_guard_rejects_real_other_task_and_attached_catalogs(string connection) =>
        Assert.Throws<ArgumentException>(() => A22SqlFactAttribute.Validate(new(connection)));

    [A22SqlFact]
    public async Task Real_SQL_HTTP_graph_criteria_changes_preserve_history_and_new_run_uses_new_aliases()
    {
        await using var env = await A22SqlEnvironment.CreateAsync();
        using var factory = env.Api(kestrel: true); using var client = factory.CreateClient();
        var server = factory.Services.GetRequiredService<IServer>(); Assert.Contains("Kestrel", server.GetType().FullName!);
        client.BaseAddress = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var request = JobRulesTests.Valid("A22 SQL graph"); request.MinYearsExperience = 0; request.MinGpa = null;
        request.Skills = [JobSkillEndpointTests.Skill("ASPNET_CORE"), JobSkillEndpointTests.Skill("SQL", "SQL Server"), JobSkillEndpointTests.Skill("GIT", "Git", "NiceToHave")];
        // Begin without aliases so that only a later criteria edit can match dot net.
        foreach (var s in request.Skills) s.Aliases = [];
        using var created = await client.PostAsJsonAsync("/api/jobs", request); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = (await created.Content.ReadFromJsonAsync<JobDto>())!; Assert.Equal(3, job.Skills.Count); Assert.Equal(2, job.MustHaveCount);
        Assert.Equal(1, await env.IntAsync($"SELECT COUNT(*) FROM dbo.Jobs WHERE JobId={job.JobId};"));
        Assert.Equal(3, await env.IntAsync($"SELECT COUNT(*) FROM dbo.JobSkills WHERE JobId={job.JobId};"));
        Assert.Equal(0, await env.IntAsync($"SELECT COUNT(*) FROM dbo.JobSkillAliases WHERE JobId={job.JobId};"));
        Assert.Equal(2, await env.IntAsync("SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN(N'FK_JobSkillAliases_JobSkills',N'FK_EvaluationSkills_JobSkills') AND delete_referential_action=0;"));
        Assert.Equal(1, await env.IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Jobs') AND name=N'UQ_Jobs_Title' AND is_unique=1 AND is_unique_constraint=0;"));
        Assert.Equal(2, await env.IntAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.Jobs') AND name IN(N'Headcount',N'ClosingDate');"));

        int applicationId, oldId;
        await using (var db = env.Context())
        {
            var cv = JobCrudEndpointTests.Cv(); cv.ExtractedText = "Có kinh nghiệm dot net, SQL Server và Git.";
            var app = new Application { JobId = job.JobId, Cv = cv, Stage = ApplicationStages.New, CreatedAt = DateTime.UtcNow };
            db.Add(app); await db.SaveChangesAsync(); applicationId = app.ApplicationId;
        }
        var offline = new OfflineEvaluator();
        oldId = await ScoreNewRunAsync(env, applicationId, offline);
        string oldResult;
        decimal oldScore;
        await using (var db = env.Context())
        {
            var old = await db.Evaluations.AsNoTracking().SingleAsync(e => e.EvaluationId == oldId);
            oldScore = old.KeywordScore!.Value; oldResult = await EvaluationJsonAsync(db, oldId);
            Assert.Equal(ScoreSources.KeywordOnly, old.ScoreSource);
            Assert.False((await db.EvaluationSkills.SingleAsync(e => e.EvaluationId == oldId && e.SkillCode == "ASPNET_CORE")).IsMatched);
        }
        var before = await env.HistoryFingerprintAsync("history-before-criteria");
        using var aliases = await client.PutAsJsonAsync($"/api/jobs/{job.JobId}/skills/aspnet_core/aliases", new ReplaceAliasesRequest { Aliases = [" DOT\tNET ", "dot net"] });
        Assert.Equal(HttpStatusCode.OK, aliases.StatusCode); Assert.Equal(["dot net"], (await aliases.Content.ReadFromJsonAsync<JobSkillDto>())!.Aliases);
        using var edit = await client.PutAsJsonAsync($"/api/jobs/{job.JobId}/skills/GIT", new UpdateJobSkillRequest { DisplayName = "Git workflow", SkillType = "MustHave" }); Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        using var editBack = await client.PutAsJsonAsync($"/api/jobs/{job.JobId}/skills/GIT", new UpdateJobSkillRequest { DisplayName = "Git", SkillType = "NiceToHave" }); Assert.Equal(HttpStatusCode.OK, editBack.StatusCode);
        using var refuse = await client.DeleteAsync($"/api/jobs/{job.JobId}/skills/ASPNET_CORE"); await JobCrudEndpointTests.AssertProblemAsync(refuse, HttpStatusCode.Conflict);
        Assert.Equal(before, await env.HistoryFingerprintAsync("history-after-criteria"));
        Assert.True((await client.GetFromJsonAsync<JobDto>($"/api/jobs/{job.JobId}"))!.HasEvaluations);
        var newId = await ScoreNewRunAsync(env, applicationId, offline);
        await using (var db = env.Context())
        {
            var current = await db.Evaluations.AsNoTracking().SingleAsync(e => e.EvaluationId == newId);
            Assert.True(current.KeywordScore > oldScore); Assert.Equal(100m, current.KeywordScore);
            Assert.True((await db.EvaluationSkills.SingleAsync(e => e.EvaluationId == newId && e.SkillCode == "ASPNET_CORE")).IsMatched);
            Assert.Equal(oldResult, await EvaluationJsonAsync(db, oldId));
            Assert.Equal(1, await db.Cvs.CountAsync()); Assert.Equal(1, await db.Applications.CountAsync()); Assert.Equal(6, await db.EvaluationSkills.CountAsync());
            await env.WriteAsync("scoring-and-history", new { OldKeyword = oldScore, NewKeyword = current.KeywordScore, OldResultUnchanged = true, BeforeHash = before, OfflineAiCalls = offline.Calls, RealGeminiCalls = 0 });
        }

        using var add = await client.PostAsJsonAsync($"/api/jobs/{job.JobId}/skills", JobSkillEndpointTests.Skill("DOCKER", "Docker", "NiceToHave")); Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        using var duplicate = await client.PostAsJsonAsync($"/api/jobs/{job.JobId}/skills", JobSkillEndpointTests.Skill("docker")); await JobCrudEndpointTests.AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "skillCode");
        using var clear = await client.PutAsJsonAsync($"/api/jobs/{job.JobId}/skills/DOCKER/aliases", new ReplaceAliasesRequest { Aliases = [] }); Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using var delete = await client.DeleteAsync($"/api/jobs/{job.JobId}/skills/DOCKER"); Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var legacy = await client.PutAsJsonAsync("/api/jobs/1/skills/CSHARP", new UpdateJobSkillRequest { DisplayName = "C# backend", SkillType = "NiceToHave" });
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode); Assert.Equal("csharp", (await legacy.Content.ReadFromJsonAsync<JobSkillDto>())!.SkillCode);
        await env.WriteAsync("http-sql", new { Create = 201, DuplicateCode = 409, Update = 200, Aliases = 200, Clear = 200, DeleteUnused = 204, DeleteUsed = 409, JobId = job.JobId, SkillCount = 3, MustHaveCount = 2 });
    }

    [A22SqlFact]
    public async Task Native_SQL_unique_constraint_resolves_two_simultaneous_skill_creates()
    {
        await using var env = await A22SqlEnvironment.CreateAsync(); var barrier = new SkillInsertBarrier();
        using var factory = env.Api(barrier); using var client = factory.CreateClient();
        var responses = await Task.WhenAll(client.PostAsJsonAsync("/api/jobs/1/skills", JobSkillEndpointTests.Skill("RACE_A22")), client.PostAsJsonAsync("/api/jobs/1/skills", JobSkillEndpointTests.Skill("race_a22")));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order());
        Assert.Equal(2, barrier.Arrivals); Assert.Equal(1, barrier.UniqueFailures);
        foreach (var response in responses) { if (response.StatusCode == HttpStatusCode.Conflict) await JobCrudEndpointTests.AssertProblemAsync(response, HttpStatusCode.Conflict, "skillCode"); response.Dispose(); }
        Assert.Equal(1, await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkills WHERE JobId=1 AND SkillCode=N'RACE_A22';"));
        Assert.Equal(1, await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkillAliases WHERE JobId=1 AND SkillCode=N'RACE_A22';"));
        await env.WriteAsync("concurrent-insert", new { barrier.Arrivals, barrier.UniqueFailures, Status = new[] { 201, 409 } });
    }

    [A22SqlFact]
    public async Task Evaluation_committed_after_delete_precheck_returns_409_and_rolls_back_alias_deletes()
    {
        await using var env = await A22SqlEnvironment.CreateAsync();
        var barrier = new SkillDeleteBarrier(); using var factory = env.Api(barrier); using var client = factory.CreateClient();
        var aliasesBefore = await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkillAliases WHERE JobId=1 AND SkillCode=N'csharp';"); Assert.True(aliasesBefore > 0);
        var deleting = client.DeleteAsync("/api/jobs/1/skills/CSHARP");
        try
        {
            await barrier.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var db = env.Context();
            var run = NewRun(); run.Status = RunStatuses.Completed;
            var evaluation = new Evaluation { Run = run, Application = new Application { JobId = 1, Cv = JobCrudEndpointTests.Cv() }, Status = EvaluationStatuses.Completed };
            evaluation.Skills.Add(new EvaluationSkill { JobId = 1, SkillCode = "csharp", IsMatched = true, Evidence = "Concurrent saved evidence" });
            db.Add(evaluation); await db.SaveChangesAsync();
        }
        finally { barrier.Release.TrySetResult(); }
        using var response = await deleting; await JobCrudEndpointTests.AssertProblemAsync(response, HttpStatusCode.Conflict);
        Assert.Equal(1, barrier.ForeignKeyFailures);
        Assert.Equal(aliasesBefore, await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkillAliases WHERE JobId=1 AND SkillCode=N'csharp';"));
        Assert.Equal(1, await env.IntAsync("SELECT COUNT(*) FROM dbo.EvaluationSkills WHERE JobId=1 AND SkillCode=N'csharp' AND Evidence=N'Concurrent saved evidence';"));
        Assert.Equal(1, await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkills WHERE JobId=1 AND SkillCode=N'csharp';"));
        await env.WriteAsync("delete-evaluation-race", new { Status = 409, barrier.ForeignKeyFailures, AliasesBefore = aliasesBefore, AliasesPreserved = true, EvaluationPreserved = true });
    }

    [A22SqlFact]
    public async Task Mid_transaction_SQL_failure_after_Job_and_skill_insert_rolls_back_the_entire_graph()
    {
        await using var env = await A22SqlEnvironment.CreateAsync(); var fault = new PartialGraphFault();
        using var factory = env.Api(fault, kestrel: true, singleBatch: true); using var client = factory.CreateClient();
        client.BaseAddress = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var before = await env.HistoryFingerprintAsync("rollback-before");
        var request = JobRulesTests.Valid("A22 partial rollback"); request.Skills = [JobSkillEndpointTests.Skill("ROLLBACK_A22")];
        using var response = await client.PostAsJsonAsync("/api/jobs", request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode); Assert.True(fault.ObservedJobAndSkillInsideTransaction); Assert.True(fault.Fired);
        Assert.Equal(5, await env.IntAsync("SELECT COUNT(*) FROM dbo.Jobs;")); Assert.Equal(29, await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkills;")); Assert.Equal(52, await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkillAliases;"));
        Assert.Equal(0, await env.IntAsync("SELECT COUNT(*) FROM dbo.Jobs WHERE Title=N'A22 partial rollback';"));
        Assert.Equal(before, await env.HistoryFingerprintAsync("rollback-after"));
        await env.WriteAsync("rollback-result", new { Status = 500, fault.Fired, fault.ObservedJobAndSkillInsideTransaction, Jobs = 5, Skills = 29, Aliases = 52, RolledBack = true });
    }

    [A22SqlFact]
    public async Task Concurrent_complete_alias_replacements_do_not_merge_the_two_lists()
    {
        await using var env = await A22SqlEnvironment.CreateAsync(); var barrier = new AliasReplaceBarrier();
        using var factory = env.Api(barrier); using var client = factory.CreateClient();
        using var clear = await client.PutAsJsonAsync("/api/jobs/1/skills/git/aliases", new ReplaceAliasesRequest { Aliases = [] }); Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var responses = await Task.WhenAll(
            client.PutAsJsonAsync("/api/jobs/1/skills/git/aliases", new ReplaceAliasesRequest { Aliases = ["alpha-a22"] }),
            client.PutAsJsonAsync("/api/jobs/1/skills/git/aliases", new ReplaceAliasesRequest { Aliases = ["beta-a22"] }));
        await env.WriteAsync("alias-race-responses", await Task.WhenAll(responses.Select(async r => new { Status = (int)r.StatusCode, Body = await r.Content.ReadAsStringAsync() })));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order());
        Assert.Equal(2, barrier.Arrivals);
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var saved = (await winner.Content.ReadFromJsonAsync<JobSkillDto>())!.Aliases;
        Assert.Single(saved);
        await using var db = env.Context(); Assert.Equal(saved, await db.JobSkillAliases.Where(a => a.JobId == 1 && a.SkillCode == "git").Select(a => a.Alias).ToListAsync());
        foreach (var response in responses) response.Dispose();
        await env.WriteAsync("concurrent-alias-replace", new { Status = new[] { 200, 409 }, barrier.Arrivals, Saved = saved, ListsMerged = false });
    }

    [A22SqlFact]
    public async Task SQL_collation_equivalent_aliases_return_409_and_roll_back_new_Job_or_skill()
    {
        await using var env = await A22SqlEnvironment.CreateAsync(); using var factory = env.Api(); using var client = factory.CreateClient();
        Assert.Equal(1, await env.IntAsync("SELECT CASE WHEN N'ss'=N'ß' THEN 1 ELSE 0 END;"));
        var skill = JobSkillEndpointTests.Skill("COLLATION_A22"); skill.Aliases = ["ss", "ß"];
        using var add = await client.PostAsJsonAsync("/api/jobs/1/skills", skill); await JobCrudEndpointTests.AssertProblemAsync(add, HttpStatusCode.Conflict, "aliases");
        var request = JobRulesTests.Valid("A22 collation graph"); request.Skills = [skill];
        using var create = await client.PostAsJsonAsync("/api/jobs", request); await JobCrudEndpointTests.AssertProblemAsync(create, HttpStatusCode.Conflict, "skills");
        Assert.Equal(5, await env.IntAsync("SELECT COUNT(*) FROM dbo.Jobs;")); Assert.Equal(29, await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkills;")); Assert.Equal(52, await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkillAliases;"));
        await env.WriteAsync("alias-collation", new { AddSkill = 409, CreateJob = 409, PartialRows = 0 });
    }

    [A22SqlFact]
    public async Task SQL_validation_failure_in_later_skill_creates_nothing_and_no_A22_migration_is_needed()
    {
        await using var env = await A22SqlEnvironment.CreateAsync(); using var factory = env.Api(); using var client = factory.CreateClient();
        var request = JobRulesTests.Valid("A22 invalid graph"); request.Skills = [JobSkillEndpointTests.Skill("VALID"), JobSkillEndpointTests.Skill("BAD", "bad", "Required")];
        using var bad = await client.PostAsJsonAsync("/api/jobs", request); await JobCrudEndpointTests.AssertProblemAsync(bad, HttpStatusCode.BadRequest, "skills[1].skillType");
        request.Skills = [JobSkillEndpointTests.Skill("SQL"), JobSkillEndpointTests.Skill(" sql ")];
        using var dup = await client.PostAsJsonAsync("/api/jobs", request); await JobCrudEndpointTests.AssertProblemAsync(dup, HttpStatusCode.BadRequest, "skills");
        request.Skills = [JobSkillEndpointTests.Skill("VALID")]; request.Skills[0].Aliases = [new string('x', 101)];
        using var alias = await client.PostAsJsonAsync("/api/jobs", request); await JobCrudEndpointTests.AssertProblemAsync(alias, HttpStatusCode.BadRequest, "skills[0].aliases");
        Assert.Equal(5, await env.IntAsync("SELECT COUNT(*) FROM dbo.Jobs;")); Assert.Equal(29, await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkills;")); Assert.Equal(52, await env.IntAsync("SELECT COUNT(*) FROM dbo.JobSkillAliases;"));
        await using var db = env.Context(); Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await env.WriteAsync("validation", new { InvalidSkill = 400, DuplicateNormalizedCode = 400, LongAlias = 400, NoPartialJob = true, PendingModelChanges = false, MigrationCount = 6 });
    }

    private static EvaluationRun NewRun() => new()
    {
        PromptVersion = new PromptVersion { VersionName = "A22 offline " + Guid.NewGuid().ToString("N"), SystemPrompt = "synthetic" }, RunType = RunTypes.Screening,
        Status = RunStatuses.Queued, TotalItems = 1, KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70, ModelName = "offline-test-only", CreatedAt = DateTime.UtcNow
    };

    private static async Task<int> ScoreNewRunAsync(A22SqlEnvironment env, int applicationId, OfflineEvaluator ai)
    {
        await using var db = env.Context(); var run = NewRun();
        var evaluation = new Evaluation { Run = run, ApplicationId = applicationId, Status = EvaluationStatuses.Pending, CreatedAt = DateTime.UtcNow };
        db.Add(evaluation); await db.SaveChangesAsync(); var id = evaluation.EvaluationId;
        var processor = new RunScoringProcessor(db, ai, Options.Create(new ScoringOptions()), NullLogger<RunScoringProcessor>.Instance);
        Assert.Equal(1, await processor.ProcessRunAsync(run.RunId, CancellationToken.None));
        Assert.Equal(EvaluationStatuses.Completed, (await db.Evaluations.AsNoTracking().SingleAsync(e => e.EvaluationId == id)).Status);
        return id;
    }

    private static async Task<string> EvaluationJsonAsync(SmartCvDbContext db, int id) => JsonSerializer.Serialize(new
    {
        Evaluation = await db.Evaluations.AsNoTracking().SingleAsync(e => e.EvaluationId == id),
        Skills = await db.EvaluationSkills.AsNoTracking().Where(e => e.EvaluationId == id).OrderBy(e => e.SkillCode)
            .Select(e => new { e.EvaluationId, e.JobId, e.SkillCode, e.IsMatched, e.Evidence }).ToListAsync()
    });

    private sealed class OfflineEvaluator : IAiEvaluator
    {
        internal int Calls { get; private set; }
        public Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; return Task.FromResult(AiEvaluationResult.Failure(AiErrorKind.Network, "A22 offline test: no external request", 0));
        }
    }
}

internal sealed class SkillInsertBarrier : SaveChangesInterceptor
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrivals, failures;
    internal int Arrivals => arrivals;
    internal int UniqueFailures => failures;
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<JobSkill>().Any(e => e.State == EntityState.Added && e.Entity.SkillCode == "RACE_A22"))
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        return result;
    }
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Exception is DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } }) Interlocked.Increment(ref failures);
        return Task.CompletedTask;
    }
}

internal sealed class SkillDeleteBarrier : SaveChangesInterceptor
{
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int ForeignKeyFailures { get; private set; }
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<JobSkill>().Any(e => e.State == EntityState.Deleted))
        {
            Ready.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        return result;
    }
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Exception is DbUpdateException { InnerException: SqlException { Number: 547 } }) ForeignKeyFailures++;
        return Task.CompletedTask;
    }
}

internal sealed class AliasReplaceBarrier : SaveChangesInterceptor
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrivals;
    internal int Arrivals => arrivals;
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<JobSkillAlias>().Any(e => e.State == EntityState.Added && e.Entity.Alias is "alpha-a22" or "beta-a22"))
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        return result;
    }
}

internal sealed class PartialGraphFault : DbCommandInterceptor
{
    internal bool ObservedJobAndSkillInsideTransaction { get; private set; }
    internal bool Fired { get; private set; }
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("INSERT INTO [JobSkillAliases]", StringComparison.Ordinal) && !Fired)
        {
            Assert.NotNull(command.Transaction);
            await using var check = command.Connection!.CreateCommand(); check.Transaction = command.Transaction;
            check.CommandText = "SELECT COUNT(*) FROM dbo.Jobs j JOIN dbo.JobSkills s ON s.JobId=j.JobId WHERE j.Title=N'A22 partial rollback' AND s.SkillCode=N'ROLLBACK_A22';";
            ObservedJobAndSkillInsideTransaction = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) == 1;
            Fired = true;
            command.CommandText = "THROW 51022, 'A22 isolated rollback test', 1;";
        }
        return result;
    }
}
