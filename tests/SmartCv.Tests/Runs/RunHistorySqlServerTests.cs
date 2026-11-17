using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Core.Privacy;
using SmartCv.Infrastructure.Privacy;

namespace SmartCv.Tests.Runs;

public class RunHistorySqlServerTests
{
    [Theory]
    [InlineData("Server=.;Database=SmartCvDb;Integrated Security=True")]
    [InlineData("Server=.;Database=SmartCv_A22_Test_abc;Integrated Security=True")]
    [InlineData("Server=.;Database=SmartCv_A25_Test_abc;AttachDbFilename=C:\\unsafe.mdf;Integrated Security=True")]
    public void Guard_rejects_user_other_task_and_attached_catalogs(string value) => Assert.Throws<ArgumentException>(() => A25SqlFactAttribute.Validate(new(value)));

    [A25SqlFact]
    public async Task SQL_HTTP_history_uses_saved_configuration_correct_filters_and_retention_privacy_without_writes()
    {
        await using var env = await A25SqlEnvironment.CreateAsync(); RunHistoryEndpointTests.Seed seed;
        await using (var db = env.Context()) seed = await RunHistoryEndpointTests.SeedAsync(db);
        var before = await env.FingerprintAsync("before-history-reads");
        var recorder = new HistoryReadRecorder(); using var factory = env.Api(recorder, kestrel: true); using var client = factory.CreateClient();
        var server = factory.Services.GetRequiredService<IServer>(); Assert.Contains("Kestrel", server.GetType().FullName!);
        client.BaseAddress = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single()); recorder.Active = true;
        var page = (await client.GetFromJsonAsync<RunPageDto>("/api/runs?page=1&from=2026-11-17&to=2026-11-17&pageSize=2"))!;
        Assert.Equal((3, 2), (page.TotalCount, page.TotalPages)); Assert.Equal(new[] { seed.Runs[3], seed.Runs[2] }, page.Items.Select(r => r.RunId));
        Assert.Equal(75m, page.Items[0].AccuracyPercent); Assert.Equal(seed.Set, page.Items[0].EvaluationSetId);
        var old = (await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{seed.MixedRun}"))!;
        Assert.Equal((seed.OldPrompt, "A25 old", "A25 OLD SAVED SYSTEM PROMPT", "saved-model-A25", .42m, .4m, .6m, 81m),
            (old.PromptVersionId, old.PromptVersionName, old.SystemPrompt, old.ModelName, old.Temperature, old.KeywordWeight, old.AiWeight, old.PassThreshold));
        var anon = old.Items.Single(i => i.EvaluationId == seed.AnonymousEvaluation); Assert.Equal(RetentionRules.AnonymizedFileName, anon.FileName); Assert.Null(anon.CandidateName); Assert.Null(anon.ErrorMessage);
        var test = (await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{seed.Runs[3]}"))!; Assert.Equal(seed.Set, test.EvaluationSetId); Assert.Equal(75m, test.AccuracyPercent);
        var errors = (await client.GetFromJsonAsync<RunFailuresDto>($"/api/runs/{seed.MixedRun}/failures"))!; Assert.Equal((2, 1, 3), (errors.FailedCount, errors.KeywordOnlyCount, errors.Items.Count));
        var erased = errors.Items.Single(i => i.EvaluationId == seed.AnonymousEvaluation); Assert.Null(erased.CvExtractionError); Assert.Null(erased.CandidateName); Assert.Equal(RetentionRules.AnonymizedFileName, erased.FileName);
        var text = await client.GetStringAsync($"/api/runs/{seed.MixedRun}/failures"); Assert.DoesNotContain(RunHistoryEndpointTests.Secret, text); Assert.DoesNotContain(RunHistoryEndpointTests.Erased, text);
        Assert.DoesNotContain("systemPrompt", text); Assert.DoesNotContain("aiRawResponse", text);
        Assert.Single((await client.GetFromJsonAsync<List<RunSummaryDto>>("/api/runs?type=Screening&status=Completed&jobId=2&from=2026-11-17&to=2026-11-17"))!);
        var combined = (await client.GetFromJsonAsync<RunPageDto>("/api/runs?page=1&type=Screening&status=Completed&jobId=2&from=2026-11-17&to=2026-11-17"))!; Assert.Equal(seed.MixedRun, Assert.Single(combined.Items).RunId);
        Assert.Empty((await client.GetFromJsonAsync<RunPageDto>("/api/runs?page=1&jobId=999999"))!.Items);
        Assert.Empty((await client.GetFromJsonAsync<RunFailuresDto>($"/api/runs/{seed.Runs[0]}/failures"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/999999/failures")).StatusCode);
        recorder.Active = false; Assert.Equal(before, await env.FingerprintAsync("after-history-reads"));
        await env.WriteAsync("http-and-sql", new { FilteredTotal = 3, PageSize = 2, OldPrompt = seed.OldPrompt, MixedRun = seed.MixedRun, Failed = 2, KeywordOnly = 1, AnonymizedProtected = true, ReadOnlyHash = before, GeminiCalls = 0 });
    }

    [A25SqlFact]
    public async Task SQL_paging_is_server_side_and_has_constant_query_count_for_1_and_100_rows()
    {
        await using var env = await A25SqlEnvironment.CreateAsync();
        await using (var db = env.Context()) await RunHistoryEndpointTests.SeedAsync(db, extraRuns: 250);
        var recorder = new HistoryReadRecorder(); using var factory = env.Api(recorder); using var client = factory.CreateClient();
        recorder.Active = true; var measures = new List<object>();
        foreach (var size in new[] { 1, 100 })
        {
            recorder.Commands.Clear(); var watch = Stopwatch.StartNew();
            var result = (await client.GetFromJsonAsync<RunPageDto>($"/api/runs?page=2&pageSize={size}"))!; watch.Stop();
            Assert.Equal(256, result.TotalCount); Assert.Equal(size, result.Items.Count); Assert.Equal(3, recorder.Commands.Count);
            Assert.Contains(recorder.Commands, c => c.Contains("COUNT(*)", StringComparison.Ordinal));
            Assert.Contains(recorder.Commands, c => c.Contains("OFFSET", StringComparison.Ordinal) && c.Contains("FETCH NEXT", StringComparison.Ordinal));
            Assert.Contains(recorder.Commands, c => c.Contains("GROUP BY", StringComparison.Ordinal));
            foreach (var sql in recorder.Commands)
            {
                Assert.DoesNotContain("SystemPrompt", sql); Assert.DoesNotContain("ExtractedText", sql); Assert.DoesNotContain("AiRawResponse", sql); Assert.DoesNotContain("OriginalFileName", sql);
            }
            measures.Add(new { PageSize = size, EfQueries = recorder.Commands.Count, ElapsedMs = watch.ElapsedMilliseconds, Returned = result.Items.Count, Sql = recorder.Commands.ToArray() });
        }
        recorder.Commands.Clear();
        var beyond = (await client.GetFromJsonAsync<RunPageDto>("/api/runs?page=2147483647&pageSize=1"))!; Assert.Empty(beyond.Items); Assert.Equal(256, beyond.TotalCount); Assert.Equal(2, recorder.Commands.Count);
        recorder.Commands.Clear(); var legacy = (await client.GetFromJsonAsync<List<RunSummaryDto>>("/api/runs?take=3"))!; Assert.Equal(3, legacy.Count); Assert.Equal(2, recorder.Commands.Count);
        await env.WriteAsync("performance", new { Runs = 256, Measurements = measures, AfterLastQueries = 2, LegacyQueries = 2, MiddlewarePrivacyLease = "Two direct ADO commands, constant per request, excluded from EF count", SlaClaim = false });
    }

    [A25SqlFact]
    public async Task SQL_cancellation_reaches_reader_and_does_not_write_or_retry()
    {
        await using var env = await A25SqlEnvironment.CreateAsync(); await using (var db = env.Context()) await RunHistoryEndpointTests.SeedAsync(db);
        var before = await env.FingerprintAsync("before-cancellation"); var recorder = new HistoryReadRecorder { Delay = true };
        using var factory = env.Api(recorder); using var client = factory.CreateClient(); recorder.Active = true;
        using var cancelled = new CancellationTokenSource(); var request = client.GetAsync("/api/runs?page=1", cancelled.Token);
        await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        Assert.True(recorder.CancellationObserved); Assert.Single(recorder.Commands); recorder.Active = false;
        Assert.Equal(before, await env.FingerprintAsync("after-cancellation"));
        await env.WriteAsync("cancellation", new { recorder.CancellationObserved, QueryAttempts = recorder.Commands.Count, Unchanged = true });
    }

    [A25SqlFact]
    public async Task HTTP_abort_cancels_a_query_observed_running_inside_SQL_Server()
    {
        await using var env = await A25SqlEnvironment.CreateAsync(); await using (var db = env.Context()) await RunHistoryEndpointTests.SeedAsync(db);
        var recorder = new NativeHistoryCancellation(); using var factory = env.Api(recorder); using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(); var reading = client.GetAsync("/api/runs?page=1", cancellation.Token);
        var session = await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var monitor = await env.OpenAsync();
        async Task<bool> Waiting()
        {
            await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id=@session AND wait_type=N'WAITFOR';", monitor);
            command.Parameters.AddWithValue("@session", session); return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
        }
        var watch = Stopwatch.StartNew();
        while (!await Waiting() && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.True(await Waiting()); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reading);
        watch.Restart(); while (await Waiting() && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.False(await Waiting()); Assert.True(recorder.CancellationObserved); Assert.Equal(1, recorder.Attempts);
        await env.WriteAsync("native-cancellation", new { ObservedSqlWaitFor = true, ServerQueryStopped = true, recorder.CancellationObserved, recorder.Attempts });
    }

    [A25SqlFact]
    public async Task Failures_obey_existing_cross_connection_privacy_lease_during_the_entire_response()
    {
        await using var env = await A25SqlEnvironment.CreateAsync(); RunHistoryEndpointTests.Seed seed;
        await using (var db = env.Context()) seed = await RunHistoryEndpointTests.SeedAsync(db);
        var recorder = new HistoryReadRecorder { Delay = true }; using var factory = env.Api(recorder); using var client = factory.CreateClient(); recorder.Active = true;
        using var cancelled = new CancellationTokenSource(); var reading = client.GetAsync($"/api/runs/{seed.MixedRun}/failures", cancelled.Token);
        await recorder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var erasureContext = env.Context(); using var lockCancellation = new CancellationTokenSource();
        var exclusive = CvPrivacyScope.BeginAsync(erasureContext, true, lockCancellation.Token);
        try { await Task.Delay(150); Assert.False(exclusive.IsCompleted); }
        finally { lockCancellation.Cancel(); cancelled.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reading);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await exclusive);
        await using var fresh = env.Context(); await using var released = await CvPrivacyScope.BeginAsync(fresh, true, CancellationToken.None); Assert.NotNull(released);
        await env.WriteAsync("privacy-lease", new { ExclusiveWaitedForRead = true, CancelledCleanly = true, LeaseReleased = true, ErasureExecuted = false });
    }
}
