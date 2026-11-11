using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartCv.Api.Background;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>
/// A17 trên SQL Server thật: mỗi test tạo rồi xóa một database SmartCv_A17_Test_{guid} riêng, dữ liệu tổng hợp,
/// không chạy tiến trình nền, không gọi Gemini. Bật bằng SMARTCV_A17_TEST_SQL_CONNECTION.
/// </summary>
public class A17SqlServerTests
{
    private const string Summary =
        "v2-tieu-chi-ro-rang đúng 80% (8/10 CV), cao hơn v1-co-ban 10 điểm phần trăm (70%, 7/10 CV). " +
        "Sửa đúng thêm 1 CV, không làm sai thêm CV nào. Bỏ sót giữ nguyên (1 → 1), chọn nhầm giảm 1 (2 → 1).";

    [A17SqlFact]
    public async Task Demo_comparison_on_sql_server_matches_expected_numbers_and_writes_nothing()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        PromptCompareSeed.Data d;
        await using (var db = env.Context()) d = await PromptCompareSeed.DemoAsync(db);
        var before = await StoredRunsAsync(env);

        var byVersion = await env.Client.GetFromJsonAsync<PromptCompareDto>(
            $"/api/prompts/compare?baseVersionId={d.PromptV1}&candidateVersionId={d.PromptV2}&evaluationSetId={d.Set}");
        var byRun = await env.Client.GetFromJsonAsync<PromptCompareDto>(
            $"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={d.RunV2}");

        foreach (var c in new[] { byVersion!, byRun! })
        {
            Assert.Equal(("Better", 10m, 0, -1), (c.Verdict, c.AccuracyDelta!.Value, c.MissedDelta, c.WrongPickDelta));
            Assert.Equal((1, 0, 9, 0), (c.FixedCount, c.BrokenCount, c.UnchangedCount, c.UnscoredCount));
            Assert.True(c.IsFairComparison);
            Assert.Empty(c.Warnings);
            Assert.Equal(Summary, c.Summary);
            Assert.Equal((70m, 80m), (c.Base.AccuracyPercent!.Value, c.Candidate.AccuracyPercent!.Value));
            Assert.Equal(("Đỗ Ngọc Hân", "Fixed", "Ghi chú HR 9"), (c.Items[0].CandidateName, c.Items[0].Change, c.Items[0].HrNote));
            Assert.Equal((75.60m, 60.20m, -15.40m), (c.Items[0].BaseScore!.Value, c.Items[0].CandidateScore!.Value, c.Items[0].ScoreDelta!.Value));
        }
        // Cache tỷ lệ đúng của A16 (null trong dữ liệu tổng hợp) không bị so sánh ghi đè
        Assert.Equal(before, await StoredRunsAsync(env));
    }

    [A17SqlFact]
    public async Task Latest_completed_selection_list_mismatch_and_cross_set_comparison_on_sql_server()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        PromptCompareSeed.Data d;
        int newest, running, mismatched, crossSet;
        await using (var db = env.Context())
        {
            d = await PromptCompareSeed.DemoAsync(db);
            var day = PromptCompareSeed.Day;
            var allWrong = Enumerable.Range(0, 10).Select(i => PromptCompareSeed.Hr[i] ? 10m : 90m).ToArray();
            var older = PromptCompareSeed.Run(d.PromptV2, d.Set, d.Apps, allWrong, day.AddMinutes(30), day.AddMinutes(40));
            var runningRun = PromptCompareSeed.Run(d.PromptV2, d.Set, d.Apps, allWrong, day.AddDays(1), null, "Running");
            var newestRun = PromptCompareSeed.Run(d.PromptV2, d.Set, d.Apps, PromptCompareSeed.V2Scores, day.AddDays(2), day.AddDays(2).AddMinutes(5));
            db.AddRange(older, runningRun);
            await db.SaveChangesAsync();
            db.Add(newestRun);
            await db.SaveChangesAsync();
            (newest, running) = (newestRun.RunId, runningRun.RunId);

            var jobId = await db.Jobs.OrderBy(j => j.JobId).Select(j => j.JobId).FirstAsync();
            var extra = PromptCompareSeed.NewApplication(jobId, "Dương Mỹ Linh");
            db.Add(extra);
            await db.SaveChangesAsync();
            db.Add(PromptCompareSeed.Label(d.Set, extra.ApplicationId, true));
            var mismatchedRun = PromptCompareSeed.Run(d.PromptV2, d.Set, d.Apps.Take(9).Append(extra.ApplicationId).ToArray(),
                PromptCompareSeed.V2Scores, day.AddHours(3), day.AddHours(3).AddMinutes(1));
            var otherSet = new EvaluationSet { Name = "Bộ CV khác A17", CreatedAt = day };
            db.AddRange(mismatchedRun, otherSet);
            await db.SaveChangesAsync();
            for (var i = 0; i < d.Apps.Length; i++) db.Add(PromptCompareSeed.Label(otherSet.EvaluationSetId, d.Apps[i], PromptCompareSeed.Hr[i]));
            var crossRun = PromptCompareSeed.Run(d.PromptV2, otherSet.EvaluationSetId, d.Apps, PromptCompareSeed.V2Scores, day, day.AddMinutes(1));
            db.Add(crossRun);
            await db.SaveChangesAsync();
            (mismatched, crossSet) = (mismatchedRun.RunId, crossRun.RunId);
        }

        var latest = await env.Client.GetFromJsonAsync<PromptCompareDto>(
            $"/api/prompts/compare?baseVersionId={d.PromptV1}&candidateVersionId={d.PromptV2}&evaluationSetId={d.Set}");
        Assert.Equal((newest, "Better", true), (latest!.Candidate.RunId, latest.Verdict, latest.IsFairComparison));

        Assert.Equal(HttpStatusCode.Conflict,
            (await env.Client.GetAsync($"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={running}")).StatusCode);

        var lists = await env.Client.GetFromJsonAsync<PromptCompareDto>($"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={mismatched}");
        Assert.Equal(("NotComparable", false, 9), (lists!.Verdict, lists.IsFairComparison, lists.Items.Count));
        Assert.Contains(lists.Warnings, w => w.Contains("Ngô Gia Khánh") && w.Contains("Dương Mỹ Linh"));

        var cross = await env.Client.GetFromJsonAsync<PromptCompareDto>($"/api/prompt-tests/compare?baseRunId={d.RunV1}&candidateRunId={crossSet}");
        Assert.Equal(("NotComparable", false, 10), (cross!.Verdict, cross.IsFairComparison, cross.Items.Count));
        Assert.Contains(cross.Warnings, w => w.Contains("Khác bộ CV đánh giá"));
    }

    [A17SqlFact]
    public async Task Keyword_only_and_failed_evaluations_on_sql_server_are_warned_not_counted_as_fixed()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        PromptCompareSeed.Data d;
        await using (var db = env.Context())
        {
            d = await PromptCompareSeed.DemoAsync(db);
            var evaluations = await db.Evaluations.Where(e => e.RunId == d.RunV2).ToListAsync();
            var keyword = evaluations.Single(e => e.ApplicationId == d.Apps[0]);
            keyword.ScoreSource = "KeywordOnly"; keyword.AiScore = null;
            var failed = evaluations.Single(e => e.ApplicationId == d.Apps[2]);
            failed.Status = "Failed"; failed.PredictedPass = null; failed.FinalScore = null; failed.ScoreSource = null;
            await db.SaveChangesAsync();
        }

        var c = await env.Client.GetFromJsonAsync<PromptCompareDto>(
            $"/api/prompts/compare?baseVersionId={d.PromptV1}&candidateVersionId={d.PromptV2}&evaluationSetId={d.Set}");

        Assert.Equal(("NotComparable", true, 0m), (c!.Verdict, c.IsFairComparison, c.AccuracyDelta!.Value));
        Assert.Equal((1, 0, 1), (c.FixedCount, c.BrokenCount, c.UnscoredCount));
        Assert.Contains(c.Warnings, w => w.Contains("chỉ được chấm bằng từ khóa"));
        Assert.Contains(c.Warnings, w => w.Contains("không chấm được"));
        Assert.Equal("Unscored", c.Items.Single(i => i.ApplicationId == d.Apps[2]).Change);
    }

    private static async Task<string> StoredRunsAsync(SqlEnvironment env)
    {
        await using var db = env.Context();
        var rows = await db.EvaluationRuns.AsNoTracking().OrderBy(r => r.RunId)
            .Select(r => $"{r.RunId}|{r.Status}|{r.AccuracyPercent}|{r.ProcessedItems}").ToListAsync();
        var evaluations = await db.Evaluations.AsNoTracking().OrderBy(e => e.EvaluationId)
            .Select(e => $"{e.EvaluationId}|{e.Status}|{e.FinalScore}|{e.PredictedPass}").ToListAsync();
        return string.Join(";", rows.Concat(evaluations));
    }

    private sealed class SqlEnvironment : IAsyncDisposable
    {
        private const string Prefix = "SmartCv_A17_Test_";
        private readonly string connection;
        private readonly WebApplicationFactory<Program> factory;
        public HttpClient Client { get; }

        private SqlEnvironment(string connection, WebApplicationFactory<Program> factory)
        {
            this.connection = connection; this.factory = factory;
            Client = factory.CreateClient();
        }

        public static async Task<SqlEnvironment> CreateAsync()
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCV_A17_TEST_SQL_CONNECTION"));
            if (!builder.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(builder.AttachDBFilename))
                throw new InvalidOperationException($"SQL tests require a designated {Prefix} database.");
            builder.InitialCatalog = Prefix + Guid.NewGuid().ToString("N");
            builder.Pooling = false; builder.ConnectTimeout = 10;
            var connection = builder.ConnectionString;
            try
            {
                await using (var setup = Options(connection)) await setup.Database.EnsureCreatedAsync();
                // Database mới kế thừa chế độ của "model"; test cần nhiều kết nối đồng thời
                var master = new SqlConnectionStringBuilder(connection) { InitialCatalog = "master" };
                await using (var sql = new SqlConnection(master.ConnectionString))
                {
                    await sql.OpenAsync();
                    await using var command = sql.CreateCommand();
                    command.CommandText = $"ALTER DATABASE [{builder.InitialCatalog}] SET MULTI_USER";
                    await command.ExecuteNonQueryAsync();
                }
                var factory = TestSupport.ApiFactory(settings: new()
                {
                    ["Scoring:WorkerEnabled"] = "false", ["Gemini:UseFake"] = "true", ["Gemini:FakeDelayMs"] = "0"
                }).WithWebHostBuilder(host =>
                {
                    host.ConfigureLogging(logging => logging.ClearProviders());
                    host.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                        services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                        services.AddDbContext<SmartCvDbContext>(options => options.UseSqlServer(connection));
                        foreach (var descriptor in services.Where(s => s.ImplementationType == typeof(RunScoringWorker)
                            || s.ImplementationType == typeof(CvExtractionWorker)).ToList()) services.Remove(descriptor);
                    });
                });
                return new(connection, factory);
            }
            catch
            {
                await using var cleanup = Options(connection);
                await cleanup.Database.EnsureDeletedAsync();
                throw;
            }
        }

        public SmartCvDbContext Context() => Options(connection);

        private static SmartCvDbContext Options(string connection) =>
            new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            await using var cleanup = Options(connection);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}

public sealed class A17SqlFactAttribute : FactAttribute
{
    public A17SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable("SMARTCV_A17_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(value))
        {
            Skip = "NOT RUN: set SMARTCV_A17_TEST_SQL_CONNECTION for a disposable SmartCv_A17_Test_ database.";
            return;
        }
        try
        {
            var builder = new SqlConnectionStringBuilder(value);
            if (!builder.InitialCatalog.StartsWith("SmartCv_A17_Test_", StringComparison.Ordinal) || !string.IsNullOrEmpty(builder.AttachDBFilename))
                Skip = "NOT RUN: SQL connection must designate SmartCv_A17_Test_ without AttachDBFilename.";
        }
        catch (ArgumentException) { Skip = "NOT RUN: SQL test connection is invalid."; }
    }
}
