using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Dashboard;

public class DashboardSqlServerTests
{
    internal static readonly DateTime T0 = new(2026, 11, 1, 17, 0, 0, DateTimeKind.Utc);

    [A19SqlFact]
    public async Task Empty_catalog_and_missing_job_have_stable_private_responses()
    {
        await using var env = await DashboardSqlEnvironment.CreateAsync();
        var result = await env.SummaryAsync();
        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.TotalCvs + result.TotalApplications + result.FailedCvs + result.ActiveJobs + result.RunningRuns + result.FlaggedUnresolved);
        Assert.Null(result.FlaggedJobId);
        Assert.Equal(4, result.Funnel.Count);
        Assert.All(result.Funnel, s => { Assert.Equal(0, s.Current); Assert.Equal(0, s.Reached); });
        Assert.Equal(6, result.RejectReasons.Count);
        Assert.All(result.RejectReasons, r => { Assert.Equal(0, r.Count); Assert.Equal(0, r.Percent); });
        Assert.Equal(0, result.ScreeningTime.Count);
        Assert.Null(result.ScreeningTime.AverageHours);
        Assert.Null(result.ScreeningTime.MedianHours);
        Assert.Empty(result.RecentRuns);
        await AssertSqlAsync(env, result);
        using var missing = await env.Client.GetAsync("/api/dashboard/summary?jobId=2147483647");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.True(missing.Headers.CacheControl!.NoStore);
        Assert.Contains("Không tìm thấy Job", (await missing.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);
    }

    [A19SqlFact]
    public async Task Cohort_filters_funnel_history_reasons_recent_runs_match_independent_SQL()
    {
        await using var env = await DashboardSqlEnvironment.CreateAsync();
        var data = await SeedAsync(env);
        env.Recorder.Commands.Clear();
        var all = await env.SummaryAsync();
        Assert.Equal(13, all.TotalApplications);
        Assert.Equal(7, all.RejectedCount);
        Assert.Equal(1, all.OnHoldCount);
        Assert.Equal(2, all.ActiveJobs);
        Assert.Equal(1, all.RunningRuns);
        Assert.Equal(new[] { 13, 5, 4, 3 }, all.Funnel.Select(s => s.Reached));
        Assert.Equal(5, all.RecentRuns.Count);
        Assert.Equal(data.Runs.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RunId).Take(5).Select(r => r.RunId), all.RecentRuns.Select(r => r.RunId));
        Assert.All(all.RecentRuns, r => Assert.Equal(DateTimeKind.Utc, r.CreatedAt.Kind));
        Assert.Equal(4, all.ScreeningTime.Count);
        Assert.Equal(2m, all.ScreeningTime.AverageHours);
        Assert.Equal(2m, all.ScreeningTime.MedianHours);
        Assert.All(all.RejectReasons, r => Assert.True(r.Count > 0));
        await AssertSqlAsync(env, all);
        foreach (var (jobId, from, to) in new (int?, string?, string?)[]
        {
            (null,"2026-11-02","2026-11-02"), (data.Jobs[0].JobId,"2026-11-02","2026-11-02"),
            (data.Jobs[1].JobId,null,null), (null,"2030-01-01",null), (null,null,"2026-11-02"),
            (data.Jobs[0].JobId,"2026-11-01","2026-11-17"), (data.Jobs[2].JobId,null,null)
        })
        {
            var filtered = await env.SummaryAsync(Query(jobId, from, to));
            await AssertSqlAsync(env, filtered, jobId, from, to);
            Assert.Equal(1, filtered.RunningRuns); // Global even when dates/job match no applications.
        }
        var day = await env.SummaryAsync("?from=2026-11-02&to=2026-11-02");
        Assert.Equal(12, day.TotalApplications); // Application at 17:00 next UTC day is excluded.
        Assert.Equal(12, day.TotalCvs); // Old uploaded CV excluded; unassigned CV included.
        var job = await env.SummaryAsync(Query(data.Jobs[0].JobId, "2026-11-02", "2026-11-02"));
        Assert.Equal(10, job.TotalCvs); // Includes old-uploaded CV through its NEW application.
        Assert.Equal(1, job.FailedCvs);
        var blank = await env.SummaryAsync("?from=2030-01-01");
        Assert.True(blank.IsEmpty);
        Assert.Equal(2, blank.ActiveJobs);
        Assert.Equal(5, blank.RecentRuns.Count); // Not a date-filtered list.
        Assert.All(env.Recorder.Commands, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
        await env.WriteEvidenceAsync("cohort", all);
    }

    [A19SqlFact]
    public async Task Latest_any_status_ties_prompt_tests_A39_and_saved_cap_match_A40_ranking()
    {
        await using var env = await DashboardSqlEnvironment.CreateAsync(settings: new() { ["Scoring:MissingMustHaveCap"] = "10" });
        await using var db = env.Context();
        var jobs = new[] { new Job { Title = "A19 flags 1" }, new Job { Title = "A19 flags 2" } };
        var prompt = new PromptVersion { VersionName = "A19 flags", SystemPrompt = "synthetic internal prompt" };
        db.AddRange(jobs); db.Add(prompt); await db.SaveChangesAsync();
        var apps = Enumerable.Range(0, 8).Select(i => App(jobs[i == 7 ? 1 : 0], T0)).ToArray();
        apps[4].Cv.AnonymizedAt = T0; apps[4].Cv.ExtractedText = null;
        apps[5].Cv.ExtractedText = new string(' ', 500); // SQL LEN trailing-space boundary must match C#.
        db.AddRange(apps); await db.SaveChangesAsync();
        var old = Run(prompt, T0);
        var newer = Run(prompt, T0.AddHours(1));
        var promptTest = Run(prompt, T0.AddHours(2));
        promptTest.RunType = RunTypes.PromptTest;
        promptTest.EvaluationSet = new EvaluationSet { Name = "A19 synthetic set" };
        db.AddRange(old, newer, promptTest); await db.SaveChangesAsync();
        foreach (var app in apps.Take(3)) db.Add(Eval(old, app, T0, ScoreSources.KeywordOnly));
        db.Add(Eval(newer, apps[0], T0.AddHours(1), status: EvaluationStatuses.Pending));
        db.Add(Eval(newer, apps[1], T0.AddHours(1), status: EvaluationStatuses.Failed));
        db.Add(Eval(newer, apps[2], T0.AddHours(1))); // Clean latest suppresses old flag.
        db.Add(Eval(old, apps[3], T0, ScoreSources.KeywordOnly));
        db.Add(Eval(promptTest, apps[3], T0.AddHours(2))); // PromptTest cannot supersede Screening.
        db.Add(Eval(old, apps[4], T0)); // Anonymized null text does not manufacture ShortText.
        db.Add(Eval(old, apps[5], T0));
        var capped = Eval(old, apps[6], T0);
        capped.KeywordScore = 70; capped.AiScore = 90; capped.FinalScore = 69;
        capped.Reasoning = "(Điểm cuối giới hạn 69 vì thiếu kỹ năng bắt buộc: SQL)";
        capped.Skills.Add(new EvaluationSkill
        {
            JobSkill = new JobSkill { Job = jobs[0], SkillCode = "SQL", DisplayName = "SQL", SkillType = SkillTypes.MustHave }, IsMatched = false
        });
        db.Add(capped);
        db.Add(Eval(old, apps[7], T0, ScoreSources.KeywordOnly));
        await db.SaveChangesAsync();
        var result = await env.SummaryAsync();
        Assert.Equal(3, result.FlaggedUnresolved);
        Assert.Equal(jobs[0].JobId, result.FlaggedJobId);
        await AssertSqlAsync(env, result);
        using var rankingResponse = await env.Client.GetAsync($"/api/runs/{old.RunId}/ranking?jobId={jobs[0].JobId}");
        Assert.True(rankingResponse.IsSuccessStatusCode, await rankingResponse.Content.ReadAsStringAsync());
        var ranking = await rankingResponse.Content.ReadFromJsonAsync<RankingResponse>();
        Assert.NotNull(ranking);
        Assert.Contains(ranking.Items.Single(i => i.ApplicationId == apps[6].ApplicationId).ReviewFlags, f => f.Code == "CappedHighAi");
        Assert.Empty(ranking.Items.Single(i => i.ApplicationId == apps[5].ApplicationId).ReviewFlags);
        // Same CreatedAt: higher EvaluationId wins even when its run was created earlier.
        var tied = Eval(newer, apps[3], T0, status: EvaluationStatuses.Failed);
        db.Add(tied); await db.SaveChangesAsync();
        var after = await env.SummaryAsync();
        Assert.Equal(2, after.FlaggedUnresolved);
        Assert.Equal(jobs[0].JobId, after.FlaggedJobId); // One each, lower JobId wins.
        await AssertSqlAsync(env, after);
        await env.WriteEvidenceAsync("flags", after);
    }

    [A19SqlFact]
    public async Task Exact_SQL_timing_handles_outlier_odd_even_negative_and_ancient_upload()
    {
        await using var env = await DashboardSqlEnvironment.CreateAsync();
        await using var db = env.Context();
        var job = new Job { Title = "A19 timing" };
        var hours = new[] { 2, 4, 48, -1 };
        var apps = hours.Select((h, i) => App(job, T0.AddDays(i))).ToArray();
        foreach (var app in apps) app.Cv.UploadedAt = T0.AddYears(-10);
        db.AddRange(apps);
        for (var i = 0; i < apps.Length; i++) apps[i].Events.Add(Event(apps[i], "New", "Reviewing", apps[i].CreatedAt.AddHours(hours[i])));
        await db.SaveChangesAsync();
        var even = await env.SummaryAsync();
        Assert.Equal(4, even.ScreeningTime.Count);
        Assert.Equal(13.5m, even.ScreeningTime.AverageHours);
        Assert.Equal(3m, even.ScreeningTime.MedianHours);
        await AssertSqlAsync(env, even);
        var odd = await env.SummaryAsync("?to=2026-11-04");
        Assert.Equal(3, odd.ScreeningTime.Count);
        Assert.Equal(18m, odd.ScreeningTime.AverageHours);
        Assert.Equal(4m, odd.ScreeningTime.MedianHours);
        await AssertSqlAsync(env, odd, to: "2026-11-04");
        // No DATEDIFF(SECOND) 68-year overflow; invalid chronology remains a zero-duration sample.
        var ancient = App(job, new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        ancient.Events.Add(Event(ancient, "New", "Reviewing", T0));
        db.Add(ancient); await db.SaveChangesAsync();
        await AssertSqlAsync(env, await env.SummaryAsync());
    }

    [A19SqlFact]
    public async Task Kestrel_smoke_covers_default_job_date_empty_invalid_and_missing_filters()
    {
        await using var env = await DashboardSqlEnvironment.CreateAsync(kestrel: true);
        Assert.Contains("Kestrel", env.ServerType);
        var empty = await env.SummaryAsync();
        Assert.True(empty.IsEmpty);
        var seed = await SeedAsync(env);
        foreach (var query in new[] { "", Query(seed.Jobs[0].JobId), "?from=2026-11-01&to=2026-11-17", "?from=2030-01-01" })
        {
            var response = await env.SummaryAsync(query);
            Assert.Equal(4, response.Funnel.Count);
            Assert.Equal(6, response.RejectReasons.Count);
        }
        using var invalid = await env.Client.GetAsync("/api/dashboard/summary?from=2026-11-18&to=2026-11-17");
        using var missing = await env.Client.GetAsync("/api/dashboard/summary?jobId=2147483647");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await AssertSqlAsync(env, await env.SummaryAsync());
        await env.WriteEvidenceAsync("kestrel-smoke", new { Address = env.Client.BaseAddress!.ToString(), ServerType = env.ServerType, Empty = 200, Default = 200, Job = 200, DateRange = 200, NoMatches = 200, Invalid = 400, Missing = 404, Workers = false });
    }

    [A19SqlFact]
    public async Task Missing_legacy_reason_does_not_change_denominator_or_trigger_read_repair()
    {
        await using var env = await DashboardSqlEnvironment.CreateAsync();
        await using var db = env.Context();
        var job = new Job { Title = "A19 legacy reason" };
        var known = App(job, T0); known.Stage = ApplicationStages.Rejected; known.RejectReason = RejectReasons.Duplicate;
        var legacy = App(job, T0);
        db.AddRange(known, legacy); await db.SaveChangesAsync();
        // Simulate pre-A35 legacy data ONLY in this generated catalog; preserve current production constraints.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Applications NOCHECK CONSTRAINT CK_Applications_RejectReason_Stage;");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Applications SET Stage=N'Rejected', RejectReason=NULL WHERE ApplicationId={legacy.ApplicationId};");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Applications WITH NOCHECK CHECK CONSTRAINT CK_Applications_RejectReason_Stage;");
        env.Recorder.Commands.Clear();
        var response = await env.SummaryAsync();
        Assert.Equal(2, response.RejectedCount);
        Assert.Equal(50m, response.RejectReasons.Single(r => r.Reason == RejectReasons.Duplicate).Percent);
        Assert.Equal(1, response.RejectReasons.Sum(r => r.Count));
        Assert.All(env.Recorder.Commands, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
        await AssertSqlAsync(env, response);
        Assert.Null(await db.Applications.AsNoTracking().Where(a => a.ApplicationId == legacy.ApplicationId).Select(a => a.RejectReason).SingleAsync());
    }

    [A19SqlFact]
    public async Task Configured_A40_thresholds_are_shared_and_unassigned_CV_is_not_empty()
    {
        await using var env = await DashboardSqlEnvironment.CreateAsync(settings: new()
        {
            ["ReviewFlags:MinTextLength"] = "600", ["ReviewFlags:ScoreGap"] = "99", ["ReviewFlags:CappedHighAiScore"] = "95"
        });
        await using var db = env.Context();
        db.Add(NewCv(T0)); await db.SaveChangesAsync();
        var uploads = await env.SummaryAsync();
        Assert.False(uploads.IsEmpty); Assert.Equal(1, uploads.TotalCvs); Assert.Equal(0, uploads.TotalApplications);
        var job = new Job { Title = "A19 option thresholds" };
        var app = App(job, T0);
        var run = Run(new PromptVersion { VersionName = "A19 options", SystemPrompt = "synthetic" }, T0);
        db.Add(Eval(run, app, T0)); await db.SaveChangesAsync();
        var result = await env.SummaryAsync();
        Assert.Equal(1, result.FlaggedUnresolved); // 500 characters triggers configured threshold 600.
        await AssertSqlAsync(env, result);
    }

    [A19SqlFact]
    public async Task Thousands_of_rows_are_batched_read_only_and_cancellable_without_PII()
    {
        await using var env = await DashboardSqlEnvironment.CreateAsync();
        await using var db = env.Context();
        var job = new Job { Title = "A19 performance" };
        var prompt = new PromptVersion { VersionName = "A19 perf", SystemPrompt = "synthetic" };
        var run = Run(prompt, T0);
        var apps = Enumerable.Range(0, 2048).Select(_ => App(job, T0)).ToArray();
        db.Add(run); db.AddRange(apps);
        foreach (var app in apps)
        {
            db.Add(Eval(run, app, T0, ScoreSources.KeywordOnly));
            app.Events.Add(Event(app, "New", "Interview", T0.AddHours(2)));
            app.Events.Add(Event(app, "Interview", "Reviewing", T0.AddHours(4)));
        }
        await db.SaveChangesAsync();
        env.Recorder.Commands.Clear();
        var stopwatch = Stopwatch.StartNew();
        using var response = await env.Client.GetAsync("/api/dashboard/summary");
        var raw = await response.Content.ReadAsStringAsync();
        var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<DashboardSummaryDto>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2048, result.FlaggedUnresolved);
        Assert.Equal(2048, result.Funnel[3].Reached);
        Assert.Equal(2m, result.ScreeningTime.MedianHours);
        Assert.InRange(env.Recorder.Commands.Count, 10, 30); // Fixed aggregates + one query per 256 latest rows, never per app.
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0, 30);
        foreach (var forbidden in new[] { "candidateName", "email", "fileName", "storedPath", "extractedText", "reasoning", "aiRawResponse", "systemPrompt", "SECRET-A19" })
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(env.Recorder.Commands, s => s.Contains("[AiRawResponse]"));
        Assert.All(env.Recorder.Commands, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
        var queries = env.Recorder.Commands.Count;
        await AssertSqlAsync(env, result);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Client.GetAsync("/api/dashboard/summary", cancellation.Token));
        env.Recorder.CancelReads = true;
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Client.GetAsync("/api/dashboard/summary", serverCancellation.Token));
        Assert.True(env.Recorder.CancellationObserved);
        env.Recorder.CancelReads = false;
        await env.WriteEvidenceAsync("performance", new { Applications = 2048, Events = 4096, Queries = queries, Milliseconds = elapsedMilliseconds, CancellationObserved = true });
    }

    private static async Task AssertSqlAsync(DashboardSqlEnvironment env, DashboardSummaryDto api, int? jobId = null, string? from = null, string? to = null)
    {
        var script = await File.ReadAllTextAsync(TestSupport.RepoFile("database/check_a19_dashboard.sql"));
        script = script.Replace("DECLARE @JobId int = NULL;", "").Replace("DECLARE @From date = NULL;", "").Replace("DECLARE @To date = NULL;", "");
        script = script.Replace("DECLARE @ScoreGap decimal(5,2) = 30;", "").Replace("DECLARE @MinTextLength int = 500;", "")
            .Replace("DECLARE @CappedHighAiScore decimal(5,2) = 80;", "");
        var auditConnection = new SqlConnectionStringBuilder(env.Connection) { ApplicationName = "SmartCv A19 SQL audit" };
        await using var connection = new SqlConnection(auditConnection.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(script, connection);
        var flags = env.FlagOptions;
        command.Parameters.AddWithValue("@ScoreGap", flags.ScoreGap);
        command.Parameters.AddWithValue("@MinTextLength", flags.MinTextLength);
        command.Parameters.AddWithValue("@CappedHighAiScore", flags.CappedHighAiScore);
        command.Parameters.Add(new SqlParameter("@JobId", System.Data.SqlDbType.Int) { Value = (object?)jobId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@From", System.Data.SqlDbType.Date) { Value = from is null ? DBNull.Value : DateTime.Parse(from) });
        command.Parameters.Add(new SqlParameter("@To", System.Data.SqlDbType.Date) { Value = to is null ? DBNull.Value : DateTime.Parse(to) });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        foreach (var (name, value) in new[]
        {
            ("TotalCvs",api.TotalCvs),("FailedCvs",api.FailedCvs),("TotalApplications",api.TotalApplications),
            ("ActiveJobs",api.ActiveJobs),("RunningRuns",api.RunningRuns),("RejectedCount",api.RejectedCount),("OnHoldCount",api.OnHoldCount)
        }) Assert.Equal(value, (int)reader[name]);
        Assert.True(await reader.NextResultAsync());
        foreach (var step in api.Funnel)
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(step.Stage, reader["Stage"]);
            Assert.Equal(step.Current, reader["Current"]);
            Assert.Equal(step.Reached, reader["Reached"]);
            Assert.Equal(step.ConversionPercent, NullableDecimal(reader, "ConversionPercent"));
        }
        Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync());
        Assert.Equal(api.ScreeningTime.Count, reader["Count"]);
        Assert.Equal(api.ScreeningTime.AverageHours, NullableDecimal(reader, "AverageHours"));
        Assert.Equal(api.ScreeningTime.MedianHours, NullableDecimal(reader, "MedianHours"));
        Assert.True(await reader.NextResultAsync());
        foreach (var reason in api.RejectReasons)
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(reason.Reason, reader["Reason"]); Assert.Equal(reason.Count, reader["Count"]); Assert.Equal(reason.Percent, reader["Percent"]);
        }
        Assert.True(await reader.NextResultAsync());
        foreach (var run in api.RecentRuns) { Assert.True(await reader.ReadAsync()); Assert.Equal(run.RunId, reader["RunId"]); }
        Assert.False(await reader.ReadAsync());
        Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync());
        Assert.Equal(api.FlaggedUnresolved, reader["FlaggedUnresolved"]);
        Assert.Equal(api.FlaggedJobId, reader["FlaggedJobId"] is DBNull ? null : (int?)reader["FlaggedJobId"]);
    }

    private static decimal? NullableDecimal(SqlDataReader reader, string column) => reader[column] is DBNull ? null : (decimal)reader[column];
    private static string Query(int? jobId = null, string? from = null, string? to = null) => $"?jobId={jobId}&from={from}&to={to}";

    private sealed record Seed(Job[] Jobs, EvaluationRun[] Runs);
    private static async Task<Seed> SeedAsync(DashboardSqlEnvironment env)
    {
        await using var db = env.Context();
        var jobs = new[] { new Job { Title = "A19 backend" }, new Job { Title = "A19 inactive", IsActive = false }, new Job { Title = "A19 empty job" } };
        var prompt = new PromptVersion { VersionName = "A19 synthetic", SystemPrompt = "SECRET-A19 system prompt" };
        db.AddRange(jobs); db.Add(prompt); await db.SaveChangesAsync();
        var apps = Enumerable.Range(0, 13).Select(i => App(jobs[i is 4 or 11 ? 1 : 0], T0.AddHours(i))).ToArray();
        apps[0].Cv.UploadedAt = T0.AddMonths(-1); apps[0].Cv.ExtractionStatus = ExtractionStatuses.Failed;
        apps[1].Stage = ApplicationStages.Reviewing;
        apps[2].Stage = ApplicationStages.Rejected; apps[2].RejectReason = RejectReasons.MissingSkills;
        apps[3].Stage = ApplicationStages.OnHold;
        apps[4].Stage = ApplicationStages.Interview;
        for (var i = 5; i <= 10; i++) { apps[i].Stage = ApplicationStages.Rejected; apps[i].RejectReason = RejectReasons.All[i - 5]; }
        apps[11].Cv = apps[0].Cv; // One CV, two jobs.
        apps[12].CreatedAt = T0.AddDays(1); apps[12].Stage = ApplicationStages.Interview;
        apps[1].Events.Add(Event(apps[1], "New", "Reviewing", apps[1].CreatedAt.AddHours(2)));
        apps[2].Events.Add(Event(apps[2], "New", "Interview", apps[2].CreatedAt.AddHours(2)));
        apps[2].Events.Add(Event(apps[2], "Interview", "New", apps[2].CreatedAt.AddHours(3)));
        apps[2].Events.Add(Event(apps[2], "New", "Interview", apps[2].CreatedAt.AddHours(3.5)));
        apps[2].Events.Add(Event(apps[2], "Interview", "Rejected", apps[2].CreatedAt.AddHours(4), RejectReasons.MissingSkills));
        apps[3].Events.Add(Event(apps[3], "Shortlisted", "OnHold", apps[3].CreatedAt.AddHours(-1)));
        apps[4].Events.Add(Event(apps[4], "New", "Interview", apps[4].CreatedAt.AddHours(4)));
        db.AddRange(apps);
        db.Add(NewCv(T0.AddHours(1))); // Unassigned.
        db.Add(NewCv(T0.AddTicks(-1))); // Outside lower boundary.
        db.Add(NewCv(T0.AddDays(1))); // Exactly outside upper boundary.
        var runs = Enumerable.Range(0, 8).Select(i => Run(prompt, T0.AddMonths(-1).AddDays(i < 6 ? i : 5))).ToArray();
        runs[7].Status = RunStatuses.Queued;
        db.AddRange(runs); await db.SaveChangesAsync();
        foreach (var run in runs) db.Add(Eval(run, apps[0], run.CreatedAt));
        db.Add(Eval(runs[0], apps[11], T0, ScoreSources.KeywordOnly));
        await db.SaveChangesAsync();
        return new(jobs, runs);
    }

    internal static Cv NewCv(DateTime uploaded) => new()
    {
        CvId = Guid.NewGuid(), OriginalFileName = "SECRET-A19.pdf", StoredPath = "SECRET-A19/path",
        ContentType = "application/pdf", FileSizeBytes = 100, FileHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
        UploadedAt = uploaded, ExtractionStatus = ExtractionStatuses.Completed, ExtractedText = new string('x', 500)
    };
    internal static Application App(Job job, DateTime created) => new() { Job = job, Cv = NewCv(T0), CreatedAt = created };
    private static ApplicationEvent Event(Application app, string from, string to, DateTime at, string? reason = null) =>
        new() { Application = app, FromStage = from, ToStage = to, RejectReason = reason, Actor = "Synthetic HR", CreatedAt = at };
    private static EvaluationRun Run(PromptVersion prompt, DateTime created) => new()
    {
        PromptVersion = prompt, RunType = RunTypes.Screening, ModelName = "synthetic-model", KeywordWeight = .3m, AiWeight = .7m,
        Status = RunStatuses.Completed, CreatedAt = created, TotalItems = 1, ProcessedItems = 1
    };
    private static Evaluation Eval(EvaluationRun run, Application app, DateTime created, string source = ScoreSources.Combined, string status = EvaluationStatuses.Completed) => new()
    {
        Run = run, Application = app, CreatedAt = created, Status = status, ScoreSource = source,
        KeywordScore = 80, AiScore = source == ScoreSources.KeywordOnly ? null : 80, FinalScore = 80,
        Reasoning = "SECRET-A19 reasoning", AiRawResponse = "SECRET-A19 raw"
    };
}

public sealed class A19SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A19_TEST_SQL_CONNECTION";
    public const string Prefix = "SmartCv_A19_Test_";
    public A19SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrEmpty(value)) { Skip = $"NOT RUN: set {Setting} for a new retained {Prefix} database."; return; }
        try { Validate(new SqlConnectionStringBuilder(value)); }
        catch (ArgumentException) { Skip = "NOT RUN: A19 connection must designate SmartCv_A19_Test_ without attached files."; }
    }
    internal static void Validate(SqlConnectionStringBuilder builder)
    {
        if (!builder.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(builder.AttachDBFilename))
            throw new ArgumentException("Only explicitly designated A19 test catalogs are allowed.");
    }
}

internal sealed class DashboardSqlEnvironment : IAsyncDisposable
{
    internal string Connection { get; }
    internal DashboardCommands Recorder { get; } = new();
    private WebApplicationFactory<Program>? factory;
    internal HttpClient Client { get; private set; } = null!;
    internal ReviewFlagOptions FlagOptions => factory!.Services.GetRequiredService<IOptions<ReviewFlagOptions>>().Value;
    internal string ServerType => factory!.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().GetType().FullName!;
    private DashboardSqlEnvironment(string connection) => Connection = connection;
    internal SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(Connection).Options);

    internal static async Task<DashboardSqlEnvironment> CreateAsync(bool kestrel = false, Dictionary<string, string?>? settings = null)
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A19SqlFactAttribute.Setting));
        A19SqlFactAttribute.Validate(builder);
        builder.InitialCatalog = A19SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N");
        builder.Pooling = false; builder.ConnectTimeout = 10;
        var env = new DashboardSqlEnvironment(builder.ConnectionString);
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("SELECT DB_ID(@catalog)", connection);
            command.Parameters.AddWithValue("@catalog", builder.InitialCatalog);
            Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync()); // Never reuse an existing catalog.
        }
        await using (var db = env.Context())
        {
            Assert.True(await db.Database.EnsureCreatedAsync());
            // EnsureCreated also inserts A3's five sample jobs. Remove ONLY that seed from this NEW catalog
            // before exercising a truly empty database. There are no CVs, applications or runs here.
            Assert.Equal(0, await db.Cvs.CountAsync());
            await db.Database.ExecuteSqlRawAsync("DELETE FROM JobSkillAliases; DELETE FROM JobSkills; DELETE FROM Jobs;");
        }
        // This workstation's model catalog is SINGLE_USER. Change only OUR newly created catalog,
        // before starting HTTP, so A39's lease connection and endpoint can coexist. Never alter model/app DB.
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand($"ALTER DATABASE [{builder.InitialCatalog}] SET MULTI_USER;", connection);
            await command.ExecuteNonQueryAsync();
        }
        await env.WriteEvidenceAsync("catalog", new { Catalog = builder.InitialCatalog, Retained = true });
        env.factory = DashboardEndpointTests.Factory().WithWebHostBuilder(b =>
        {
            if (settings is not null) b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            b.ConfigureLogging(l => l.ClearProviders());
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                services.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(env.Connection).AddInterceptors(env.Recorder));
                foreach (var descriptor in services.Where(d => d.ImplementationType is { } type && typeof(BackgroundService).IsAssignableFrom(type)).ToArray())
                    services.Remove(descriptor);
            });
        });
        if (kestrel) env.factory.UseKestrel(0);
        env.Client = env.factory.CreateClient();
        return env;
    }

    internal async Task<DashboardSummaryDto> SummaryAsync(string query = "")
    {
        using var response = await Client.GetAsync("/api/dashboard/summary" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync<DashboardSummaryDto>())!;
    }
    internal async Task WriteEvidenceAsync(string name, object value)
    {
        var path = TestSupport.RepoFile("artifacts/a19-sql");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, new SqlConnectionStringBuilder(Connection).InitialCatalog + "-" + name + ".json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        // Intentionally retained: no EnsureDeleted, DROP DATABASE, CV cleanup or migrations.
    }
}

internal sealed class DashboardCommands : DbCommandInterceptor
{
    internal ConcurrentQueue<string> Commands { get; } = new();
    internal bool CancelReads { get; set; }
    internal bool CancellationObserved { get; private set; }
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Commands.Enqueue(command.CommandText);
        if (CancelReads)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = cancellationToken.IsCancellationRequested; throw; }
        }
        return result;
    }
}
