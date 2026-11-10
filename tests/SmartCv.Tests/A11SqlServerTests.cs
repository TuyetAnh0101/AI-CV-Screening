using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Api.Background;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

/// <summary>
/// A11 trên SQL Server THẬT: worker giữ khóa sp_getapplock trên một kết nối và xử lý lượt trên kết nối thứ hai.
/// Mỗi test tạo database tạm SmartCv_A11_Test_{guid} rồi xóa; dữ liệu CV tổng hợp; AI GIẢ (không gọi Gemini).
/// Đặt SMARTCV_A11_TEST_SQL_CONNECTION, ví dụ:
/// Server=localhost\BB;Database=SmartCv_A11_Test_;Trusted_Connection=True;TrustServerCertificate=True
/// </summary>
public class A11SqlServerTests
{
    private const string WorkerLockResource = "SmartCv.RunScoringWorker";

    // ---------- Luồng như lượt #11: 3 CV, POST /api/runs → Queued → Running → Completed ----------

    [A11SqlFact]
    public async Task Three_cv_run_created_by_api_is_scored_with_visible_progress_and_lock_is_released()
    {
        await using var db = await TestDatabase.CreateAsync(multiUser: true);
        var ai = new AiCalls();
        var logs = new LogSink();
        var applications = await db.SeedApplicationsAsync(3);

        int runId;
        var snapshots = new List<(string Status, int Processed, int Failed)>();
        RunProgressDto final;
        await using (var host = db.Host(ai, logs, fakeDelayMs: 400))
        {
            using var client = host.CreateClient();
            using var created = await client.PostAsJsonAsync("/api/runs", new StartRunRequest { ApplicationIds = applications });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var start = (await created.Content.ReadFromJsonAsync<RunProgressDto>())!;
            Assert.Equal((RunStatuses.Queued, 3, 0), (start.Status, start.TotalItems, start.ProcessedItems));
            runId = start.RunId;

            final = await PollAsync(client, runId, snapshots, r => r.Status == RunStatuses.Completed, TimeSpan.FromSeconds(90));
        }

        // Queued → Running → Completed, tiến độ tăng dần theo từng CV đã lưu
        Assert.Contains(snapshots, s => s.Status == RunStatuses.Running);
        Assert.Equal(snapshots.Select(s => s.Processed).OrderBy(x => x), snapshots.Select(s => s.Processed));
        Assert.Contains(snapshots, s => s.Status == RunStatuses.Running && s.Processed is 1 or 2);
        Assert.Equal((3, 0, 100m), (final.ProcessedItems, final.FailedItems, final.Percent));
        Assert.NotNull(final.CompletedAt);
        Assert.All(final.Items!, i =>
        {
            Assert.Equal(EvaluationStatuses.Completed, i.Status);
            Assert.Equal(ScoreSources.Combined, i.ScoreSource);
            Assert.NotNull(i.FinalScore);
        });
        Assert.Equal(3, ai.Completed);   // mỗi CV gọi AI (giả) đúng một lần

        // Dữ liệu đã lưu trong database khớp kết quả API
        await using (var check = db.Context())
        {
            var rows = await check.Evaluations.AsNoTracking().Where(e => e.RunId == runId).ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(EvaluationStatuses.Completed, r.Status));
        }

        Assert.Contains(logs.Messages, m => m.Contains("MULTI_USER") && m.Contains("dùng được"));
        // Host đã dừng: khóa worker đã nhả, không còn kết nối nào tới database tạm
        Assert.Equal(0, await db.WorkerLockCountAsync());
        Assert.Equal(0, await db.SessionCountAsync());
    }

    // ---------- Nguyên nhân lượt #11 kẹt: database SINGLE_USER ----------

    [A11SqlFact]
    public async Task Single_user_database_is_diagnosed_without_touching_runs_and_worker_recovers_after_multi_user()
    {
        await using var db = await TestDatabase.CreateAsync(multiUser: false);
        var ai = new AiCalls();
        var logs = new LogSink();
        var applications = await db.SeedApplicationsAsync(3);
        var runId = await db.SeedQueuedRunAsync(applications);
        Assert.Equal("SINGLE_USER", await db.UserAccessAsync());

        await using (var host = db.Host(ai, logs, fakeDelayMs: 0))
        {
            _ = host.Services;   // khởi động host + worker

            // 1. Lúc khởi động: báo rõ database và chế độ truy cập, kèm cách sửa
            await WaitUntilAsync(() => logs.Messages.Any(m => m.Contains("KHÔNG chạy được") && m.Contains(db.Name) && m.Contains("SINGLE_USER")),
                TimeSpan.FromSeconds(30), "chưa có log chẩn đoán SINGLE_USER");
            // 2. Vòng chấm thất bại vì không mở được kết nối thứ hai: log phân loại, có mã lỗi SQL
            //    (SINGLE_USER: kết nối thứ hai bị từ chối 4060 hoặc treo tới hết ConnectTimeout -2, mỗi lần thử tới ~15 s)
            await WaitUntilAsync(() => logs.Messages.Any(m => m.Contains("không mở được database") && m.Contains(db.Name) && m.Contains("SINGLE_USER")),
                TimeSpan.FromSeconds(120), "chưa có log lỗi mở database");
            Assert.Equal(0, ai.Started);   // Gemini (giả) chưa hề được gọi: không phải lỗi AI
            // Log của worker chỉ có tên database, không có connection string
            var secret = new System.Text.RegularExpressions.Regex(@"(Password|Pwd|Trusted_Connection|Integrated Security|User ID)\s*=",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Assert.DoesNotContain(logs.Messages, m => secret.IsMatch(m));

            // 3. Sửa database (như đã làm với SmartCvDb), KHÔNG khởi động lại API: worker tự chấm tiếp
            await db.SetMultiUserWhenPossibleAsync(TimeSpan.FromSeconds(60));
            await WaitUntilAsync(() => ai.Completed == 3, TimeSpan.FromSeconds(120), "worker chưa phục hồi sau khi đổi MULTI_USER");
        }

        await using var check = db.Context();
        var run = await check.EvaluationRuns.AsNoTracking().SingleAsync(r => r.RunId == runId);
        Assert.Equal((RunStatuses.Completed, 3, 0), (run.Status, run.ProcessedItems, run.FailedItems));
        // Trong lúc lỗi, không hồ sơ nào bị đánh dấu Failed hay chấm hai lần
        Assert.Equal(3, ai.Completed);
        Assert.All(await check.Evaluations.AsNoTracking().Where(e => e.RunId == runId).ToListAsync(),
            e => Assert.Equal(EvaluationStatuses.Completed, e.Status));
    }

    // ---------- Không xử lý trùng khi chạy nhiều worker (nhiều instance API) ----------

    [A11SqlFact]
    public async Task Two_workers_on_one_database_never_score_the_same_cv_twice()
    {
        await using var db = await TestDatabase.CreateAsync(multiUser: true);
        var ai = new AiCalls();
        var applications = await db.SeedApplicationsAsync(6);
        var runId = await db.SeedQueuedRunAsync(applications);

        await using (var first = db.Host(ai, new LogSink(), fakeDelayMs: 200))
        await using (var second = db.Host(ai, new LogSink(), fakeDelayMs: 200))
        {
            _ = first.Services;
            _ = second.Services;
            await WaitUntilAsync(async () => (await db.RunAsync(runId)).Status == RunStatuses.Completed,
                TimeSpan.FromSeconds(90), "lượt chưa xong");
        }

        Assert.Equal(6, ai.Completed);
        Assert.All(ai.TextsScored.Values, count => Assert.Equal(1, count));   // mỗi CV đúng một lần
        var run = await db.RunAsync(runId);
        Assert.Equal((6, 0), (run.ProcessedItems, run.FailedItems));
    }

    // ---------- Tắt API giữa chừng: khởi động lại chấm tiếp, không mất trạng thái, không chấm lại ----------

    [A11SqlFact]
    public async Task Restart_in_the_middle_keeps_saved_results_and_scores_only_pending_items()
    {
        await using var db = await TestDatabase.CreateAsync(multiUser: true);
        var ai = new AiCalls();
        var applications = await db.SeedApplicationsAsync(4);
        var runId = await db.SeedQueuedRunAsync(applications);

        await using (var first = db.Host(ai, new LogSink(), fakeDelayMs: 700))
        {
            _ = first.Services;
            await WaitUntilAsync(async () => (await db.RunAsync(runId)).ProcessedItems >= 1, TimeSpan.FromSeconds(60), "chưa chấm được hồ sơ nào");
        }

        var stopped = await db.RunAsync(runId);
        Assert.Equal(RunStatuses.Running, stopped.Status);   // giống lượt #4: Running, còn hồ sơ Pending
        Assert.InRange(stopped.ProcessedItems, 1, 3);
        var scoredBefore = ai.Completed;

        await using (var second = db.Host(ai, new LogSink(), fakeDelayMs: 0))
        {
            _ = second.Services;
            await WaitUntilAsync(async () => (await db.RunAsync(runId)).Status == RunStatuses.Completed, TimeSpan.FromSeconds(60), "chưa chấm tiếp");
        }

        Assert.Equal(4, ai.Completed);                                 // tổng số lần chấm xong = số CV
        Assert.Equal(4 - stopped.ProcessedItems, ai.Completed - scoredBefore);
        Assert.All(ai.TextsScored.Values, count => Assert.Equal(1, count));
        Assert.Equal((4, 0), ((await db.RunAsync(runId)).ProcessedItems, (await db.RunAsync(runId)).FailedItems));
    }

    // ---------- Khóa worker: độc quyền, nhả khi dispose, không giữ kết nối ----------

    [A11SqlFact]
    public async Task Worker_lock_is_exclusive_and_released_with_its_connection()
    {
        await using var db = await TestDatabase.CreateAsync(multiUser: true);
        await using var ctx1 = db.Context();
        await using var ctx2 = db.Context();
        var p1 = Processor(ctx1);
        var p2 = Processor(ctx2);

        var first = await p1.TryAcquireWorkerLockAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await p2.TryAcquireWorkerLockAsync(CancellationToken.None));   // instance khác đang giữ
        Assert.Equal(System.Data.ConnectionState.Closed, ctx2.Database.GetDbConnection().State);   // không giữ được thì không giữ kết nối
        Assert.Equal(1, await db.WorkerLockCountAsync());

        // Trong lúc giữ khóa, DbContext khác vẫn đọc được (cần MULTI_USER)
        Assert.True(await ctx2.EvaluationRuns.CountAsync() >= 0);

        await first!.DisposeAsync();
        Assert.Equal(System.Data.ConnectionState.Closed, ctx1.Database.GetDbConnection().State);
        Assert.Equal(0, await db.WorkerLockCountAsync());

        var second = await p2.TryAcquireWorkerLockAsync(CancellationToken.None);
        Assert.NotNull(second);
        await second!.DisposeAsync();
        Assert.Equal(0, await db.WorkerLockCountAsync());
    }

    [A11SqlFact]
    public async Task Diagnostics_read_user_access_of_the_connected_database()
    {
        await using var single = await TestDatabase.CreateAsync(multiUser: false);
        await using (var ctx = single.Context())
        {
            var access = await WorkerDatabaseDiagnostics.ReadUserAccessAsync(ctx, CancellationToken.None);
            Assert.Equal("SINGLE_USER", access);
            Assert.False(WorkerDatabaseDiagnostics.SupportsWorker(access));
            Assert.Equal(single.Name, WorkerDatabaseDiagnostics.DatabaseName(ctx));
        }

        await using var multi = await TestDatabase.CreateAsync(multiUser: true);
        await using var ctx2 = multi.Context();
        Assert.True(WorkerDatabaseDiagnostics.SupportsWorker(await WorkerDatabaseDiagnostics.ReadUserAccessAsync(ctx2, CancellationToken.None)));
    }

    // =====================================================================

    private static RunScoringProcessor Processor(SmartCvDbContext db) =>
        new(db, new NoAi(), Options.Create(new ScoringOptions()), NullLogger<RunScoringProcessor>.Instance);

    private sealed class NoAi : IAiEvaluator
    {
        public Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("Không được gọi AI trong test khóa");
    }

    private static async Task<RunProgressDto> PollAsync(HttpClient client, int runId, List<(string, int, int)> snapshots,
        Func<RunProgressDto, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var run = (await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{runId}"))!;
            snapshots.Add((run.Status, run.ProcessedItems, run.FailedItems));
            if (done(run)) return run;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Lượt {runId}: {run.Status} {run.ProcessedItems}/{run.TotalItems}");
            await Task.Delay(100);
        }
    }

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout, because);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            await Task.Delay(200);
        }
    }

    /// <summary>AI giả đếm số lần gọi. Completed chỉ tăng khi trả kết quả (lần bị hủy lúc tắt host không tính).</summary>
    private sealed class AiCalls
    {
        private int started, completed;
        public int Started => Volatile.Read(ref started);
        public int Completed => Volatile.Read(ref completed);
        public ConcurrentDictionary<string, int> TextsScored { get; } = new();

        public IAiEvaluator Wrap(IAiEvaluator inner) => new Counting(inner, this);

        private sealed class Counting(IAiEvaluator inner, AiCalls calls) : IAiEvaluator
        {
            public async Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken ct = default)
            {
                Interlocked.Increment(ref calls.started);
                var result = await inner.EvaluateAsync(request, ct);
                Interlocked.Increment(ref calls.completed);
                calls.TextsScored.AddOrUpdate(request.CvText, 1, (_, n) => n + 1);
                return result;
            }
        }
    }

    private sealed class LogSink : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(this);
        public void Dispose() { }

        private sealed class Sink(LogSink owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(level)) owner.Messages.Enqueue($"{level}: {formatter(state, ex)}");
            }
        }
    }

    /// <summary>Database tạm. SQL Server tạo database mới theo database hệ thống model, nên chế độ được đặt rõ ràng.</summary>
    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string connection;
        private readonly string master;
        public string Name { get; }

        private TestDatabase(string connection, string master, string name) { this.connection = connection; this.master = master; Name = name; }

        public static async Task<TestDatabase> CreateAsync(bool multiUser)
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCV_A11_TEST_SQL_CONNECTION"));
            if (!builder.InitialCatalog.StartsWith("SmartCv_A11_Test_", StringComparison.Ordinal) || !string.IsNullOrEmpty(builder.AttachDBFilename))
                throw new InvalidOperationException("SQL tests require a designated SmartCv_A11_Test_ database.");
            builder.InitialCatalog = "SmartCv_A11_Test_" + Guid.NewGuid().ToString("N");
            builder.Pooling = false;   // đóng là trả kết nối ngay: cần cho database SINGLE_USER
            builder.ConnectTimeout = 15;
            var masterConnection = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
            var db = new TestDatabase(builder.ConnectionString, masterConnection, builder.InitialCatalog);
            try
            {
                await using (var setup = db.Context()) await setup.Database.EnsureCreatedAsync();
                await db.MasterAsync($"ALTER DATABASE [{db.Name}] SET {(multiUser ? "MULTI_USER" : "SINGLE_USER")}");
                return db;
            }
            catch
            {
                await db.DisposeAsync();
                throw;
            }
        }

        public SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);

        public WebApplicationFactory<Program> Host(AiCalls ai, LogSink logs, int fakeDelayMs) =>
            TestSupport.ApiFactory(settings: new()
            {
                ["Scoring:WorkerEnabled"] = "true", ["Scoring:PollIntervalSeconds"] = "1",
                ["Gemini:UseFake"] = "true", ["Gemini:FakeDelayMs"] = fakeDelayMs.ToString()
            }).WithWebHostBuilder(host =>
            {
                host.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddProvider(logs); });
                host.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                    services.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(connection));
                    services.RemoveAll<IAiEvaluator>();
                    services.AddTransient(sp => ai.Wrap(sp.GetRequiredService<FakeAiEvaluator>()));
                });
            });

        /// <summary>CV tổng hợp đã đọc chữ xong, mỗi CV một đoạn chữ riêng (để đếm số lần chấm từng CV).</summary>
        public async Task<List<int>> SeedApplicationsAsync(int count)
        {
            await using var db = Context();
            var job = await db.Jobs.Include(j => j.Skills).OrderBy(j => j.JobId).FirstAsync();
            if (!await db.PromptVersions.AnyAsync(p => p.IsActive))
                db.PromptVersions.Add(new PromptVersion { VersionName = "a11-sql", SystemPrompt = "SYNTHETIC HR", IsActive = true, CreatedAt = DateTime.UtcNow });
            var applications = Enumerable.Range(1, count).Select(i =>
            {
                var id = Guid.NewGuid();
                return new Application
                {
                    JobId = job.JobId, CreatedAt = DateTime.UtcNow, Cv = new Cv
                    {
                        CvId = id, OriginalFileName = $"a11-cv-{i}.pdf", StoredPath = "synthetic/" + id.ToString("N"),
                        ContentType = "application/pdf", FileSizeBytes = 100,
                        FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(),
                        ExtractedText = $"CV {i} {id:N}. GPA 3.2. 2 năm kinh nghiệm. " + string.Join(". ", job.Skills.Select(s => s.DisplayName)),
                        ExtractionStatus = ExtractionStatuses.Completed, ExtractionMethod = "PdfText", UploadedAt = DateTime.UtcNow
                    }
                };
            }).ToList();
            db.Applications.AddRange(applications);
            await db.SaveChangesAsync();
            return applications.Select(a => a.ApplicationId).ToList();
        }

        public async Task<int> SeedQueuedRunAsync(IReadOnlyCollection<int> applicationIds)
        {
            await using var db = Context();
            var prompt = await db.PromptVersions.FirstAsync(p => p.IsActive);
            var run = new EvaluationRun
            {
                RunType = RunTypes.Screening, PromptVersionId = prompt.PromptVersionId, ModelName = "gemini-2.5-flash",
                Temperature = 0.2m, KeywordWeight = 0.3m, AiWeight = 0.7m, PassThreshold = 70m,
                Status = RunStatuses.Queued, TotalItems = applicationIds.Count, CreatedAt = DateTime.UtcNow,
                Evaluations = applicationIds.Select(id => new Evaluation { ApplicationId = id, Status = EvaluationStatuses.Pending, CreatedAt = DateTime.UtcNow }).ToList()
            };
            db.EvaluationRuns.Add(run);
            await db.SaveChangesAsync();
            return run.RunId;
        }

        public async Task<EvaluationRun> RunAsync(int runId)
        {
            await using var db = Context();
            return await db.EvaluationRuns.AsNoTracking().SingleAsync(r => r.RunId == runId);
        }

        public async Task<string?> UserAccessAsync() =>
            (string?)await MasterScalarAsync($"SELECT user_access_desc FROM sys.databases WHERE name = N'{Name}'");

        public async Task<int> WorkerLockCountAsync() => (int)(await MasterScalarAsync(
            $"SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND resource_database_id = DB_ID(N'{Name}') " +
            $"AND resource_description LIKE N'%{WorkerLockResource}%'"))!;

        public async Task<int> SessionCountAsync() =>
            (int)(await MasterScalarAsync($"SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID(N'{Name}')"))!;

        /// <summary>Đổi sang MULTI_USER khi không còn ai đang giữ phiên duy nhất (không cưỡng chế ngắt kết nối).</summary>
        public async Task SetMultiUserWhenPossibleAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                try { await MasterAsync($"ALTER DATABASE [{Name}] SET MULTI_USER"); return; }
                catch (SqlException) when (DateTime.UtcNow < deadline) { await Task.Delay(100); }
            }
        }

        private async Task MasterAsync(string sql)
        {
            await using var c = new SqlConnection(master);
            await c.OpenAsync();
            await using var command = c.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private async Task<object?> MasterScalarAsync(string sql)
        {
            await using var c = new SqlConnection(master);
            await c.OpenAsync();
            await using var command = c.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            // Xóa database tạm (EF tự đặt SINGLE_USER WITH ROLLBACK IMMEDIATE trên CHÍNH database tạm này rồi DROP)
            await using var cleanup = Context();
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}

public sealed class A11SqlFactAttribute : FactAttribute
{
    public A11SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable("SMARTCV_A11_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(value))
        {
            Skip = "NOT RUN: set SMARTCV_A11_TEST_SQL_CONNECTION for a disposable SmartCv_A11_Test_ database.";
            return;
        }
        try
        {
            var builder = new SqlConnectionStringBuilder(value);
            if (!builder.InitialCatalog.StartsWith("SmartCv_A11_Test_", StringComparison.Ordinal) || !string.IsNullOrEmpty(builder.AttachDBFilename))
                Skip = "NOT RUN: SQL connection must designate SmartCv_A11_Test_ without AttachDBFilename.";
        }
        catch (ArgumentException) { Skip = "NOT RUN: SQL test connection is invalid."; }
    }
}
