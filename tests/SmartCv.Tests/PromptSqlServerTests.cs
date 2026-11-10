using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
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
using SmartCv.Api.Background;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>
/// Optional real SQL Server checks. Set SMARTCV_TEST_SQL_CONNECTION to a development connection
/// whose database name starts with SmartCv_A14_Test_. Every test creates and drops its own
/// suffixed database; neither SmartCvDb nor the database named in the setting is modified.
/// </summary>
public class PromptSqlServerTests
{
    [PromptSqlFact]
    public async Task Create_read_validation_conflicts_and_vietnamese_round_trip_on_sql_server()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        using var absent = await env.Client.GetAsync("/api/prompts/active");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await env.Client.GetAsync("/api/prompts/999999")).StatusCode);
        var request = new CreatePromptVersionRequest
        {
            VersionName = "v1-tiếng-Việt", SystemPrompt = "Đánh giá kỹ năng và bằng chứng từ hồ sơ.",
            ChangeNote = "Bổ sung tiêu chí tuyển dụng"
        };
        using var created = await env.Client.PostAsJsonAsync("/api/prompts", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = (await created.Content.ReadFromJsonAsync<PromptVersionDto>())!;
        Assert.False(dto.IsActive);
        Assert.Equal(request.ChangeNote, dto.ChangeNote);
        var read = (await env.Client.GetFromJsonAsync<PromptVersionDto>($"/api/prompts/{dto.PromptVersionId}"))!;
        Assert.Equal(request.SystemPrompt, read.SystemPrompt);
        Assert.Equal(HttpStatusCode.Conflict, (await env.Client.PostAsJsonAsync("/api/prompts", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await env.Client.PostAsJsonAsync("/api/prompts",
            new CreatePromptVersionRequest { VersionName = "v2", SystemPrompt = "Đánh giá  kỹ năng\n và bằng chứng từ hồ sơ." })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await env.Client.PostAsJsonAsync("/api/prompts",
            new CreatePromptVersionRequest { VersionName = "bad", SystemPrompt = " " })).StatusCode);
        await using var db = env.Context();
        Assert.Equal(1, await db.PromptVersions.CountAsync());
    }

    [PromptSqlFact]
    public async Task Latest_completed_prompt_test_accuracy_uses_relational_query_and_ignores_screening()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        int id;
        await using (var db = env.Context())
        {
            var prompt = NewPrompt("history");
            var set = new EvaluationSet { Name = "synthetic evaluation set" };
            var now = DateTime.UtcNow;
            prompt.EvaluationRuns = new List<EvaluationRun>
            {
                Run("PromptTest", "Completed", 45m, now.AddHours(-2), set),
                Run("PromptTest", "Completed", 87.5m, now.AddHours(-1), set),
                Run("PromptTest", "Running", 99m, now, set),
                Run("Screening", "Completed", 100m, now, null)
            };
            db.Add(prompt);
            await db.SaveChangesAsync();
            id = prompt.PromptVersionId;
        }
        var detail = (await env.Client.GetFromJsonAsync<PromptVersionDto>($"/api/prompts/{id}"))!;
        Assert.True(detail.HasRuns);
        Assert.Equal(87.5m, detail.LatestAccuracyPercent);
        Assert.Equal(id, Assert.Single((await env.Client.GetFromJsonAsync<List<PromptVersionDto>>("/api/prompts"))!).PromptVersionId);
        await using (var db = env.Context())
        {
            var set = await db.EvaluationSets.SingleAsync();
            var latestWithoutAccuracy = Run("PromptTest", "Completed", null, DateTime.UtcNow.AddMinutes(1), set);
            latestWithoutAccuracy.PromptVersionId = id;
            db.Add(latestWithoutAccuracy);
            await db.SaveChangesAsync();
        }
        Assert.Null((await env.Client.GetFromJsonAsync<PromptVersionDto>($"/api/prompts/{id}"))!.LatestAccuracyPercent);
    }

    [PromptSqlFact]
    public async Task Activation_switches_exactly_one_active_and_database_enforces_filtered_unique_index()
    {
        await using var env = await SqlEnvironment.CreateAsync();
        var ids = await env.SeedPromptsAsync();
        using var response = await env.Client.PostAsync($"/api/prompts/{ids.New}/activate", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var repeated = await env.Client.PostAsync($"/api/prompts/{ids.New}/activate", null);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(ids.New, (await env.Client.GetFromJsonAsync<PromptVersionDto>("/api/prompts/active"))!.PromptVersionId);
        Assert.Equal(HttpStatusCode.NotFound, (await env.Client.PostAsync("/api/prompts/999999/activate", null)).StatusCode);
        await using var db = env.Context();
        Assert.Equal(1, await db.PromptVersions.CountAsync(p => p.IsActive));
        await db.Database.OpenConnectionAsync();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PromptVersions') AND name = N'UX_PromptVersions_OneActive' AND is_unique = 1 AND has_filter = 1";
            Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        var forbidden = NewPrompt("second-active");
        forbidden.IsActive = true;
        db.Add(forbidden);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<SqlException>(failure.InnerException);
        Assert.Contains(((SqlException)failure.InnerException!).Number, new[] { 2601, 2627 });
    }

    [PromptSqlFact]
    public async Task Failure_between_activation_updates_rolls_back_previous_active_prompt()
    {
        var fault = new ActivationFaultInterceptor();
        await using var env = await SqlEnvironment.CreateAsync(fault);
        var ids = await env.SeedPromptsAsync();
        fault.Armed = true;
        using var response = await env.Client.PostAsync($"/api/prompts/{ids.New}/activate", null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(2, fault.Updates);
        await using var db = env.Context();
        Assert.Equal(ids.Old, (await db.PromptVersions.SingleAsync(p => p.IsActive)).PromptVersionId);
        Assert.False((await db.PromptVersions.SingleAsync(p => p.PromptVersionId == ids.New)).IsActive);
    }

    [PromptSqlFact]
    public async Task Concurrent_activation_requests_leave_one_committed_active_version()
    {
        await using var env = await SqlEnvironment.CreateAsync(new ActivationBarrierInterceptor());
        var ids = await env.SeedPromptsAsync();
        var responses = await Task.WhenAll(
            env.Client.PostAsync($"/api/prompts/{ids.Old}/activate", null),
            env.Client.PostAsync($"/api/prompts/{ids.New}/activate", null));
        try
        {
            Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
            foreach (var response in responses)
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                    await response.Content.ReadAsStringAsync());
            await using var db = env.Context();
            Assert.Equal(1, await db.PromptVersions.CountAsync(p => p.IsActive));
            Assert.Contains((await db.PromptVersions.SingleAsync(p => p.IsActive)).PromptVersionId, new[] { ids.Old, ids.New });
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private static PromptVersion NewPrompt(string name) => new()
    {
        VersionName = name, SystemPrompt = $"Synthetic content: {name}", CreatedAt = DateTime.UtcNow
    };

    private static EvaluationRun Run(string type, string status, decimal? accuracy, DateTime completed, EvaluationSet? set) => new()
    {
        RunType = type, Status = status, AccuracyPercent = accuracy, CompletedAt = completed,
        CreatedAt = completed.AddMinutes(-1), EvaluationSet = set, ModelName = "synthetic-model",
        KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70m
    };

    private sealed class ActivationFaultInterceptor : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int Updates { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("[PromptVersions]", StringComparison.OrdinalIgnoreCase)
                && ++Updates == 2)
                throw new InvalidOperationException("Synthetic activation failure after disabling previous prompt.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ActivationBarrierInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int updates;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("[PromptVersions]", StringComparison.OrdinalIgnoreCase))
            {
                var arrived = Interlocked.Increment(ref updates);
                if (arrived == 2) ready.TrySetResult();
                // Both requests must reach their first UPDATE before either is allowed to execute it.
                if (arrived <= 2) await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private sealed class SqlEnvironment : IAsyncDisposable
    {
        private readonly string connection;
        private readonly WebApplicationFactory<Program> factory;
        public HttpClient Client { get; }

        private SqlEnvironment(string connection, WebApplicationFactory<Program> factory)
        {
            this.connection = connection;
            this.factory = factory;
            Client = factory.CreateClient();
        }

        public static async Task<SqlEnvironment> CreateAsync(DbCommandInterceptor? interceptor = null)
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SMARTCV_TEST_SQL_CONNECTION"));
            if (!builder.InitialCatalog.StartsWith("SmartCv_A14_Test_", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(builder.AttachDBFilename))
                throw new InvalidOperationException("SQL tests require an explicitly designated SmartCv_A14_Test_ database.");
            // Use only a generated, isolated database. The configured database is never changed or dropped.
            builder.InitialCatalog = "SmartCv_A14_Test_" + Guid.NewGuid().ToString("N");
            builder.ConnectTimeout = 10;
            // Release setup connections fully before configuring/dropping the disposable database.
            builder.Pooling = false;
            var connection = builder.ConnectionString;
            await using (var setup = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options))
                await setup.Database.EnsureCreatedAsync();
            // A local server's model database may be SINGLE_USER. Change only this generated test DB.
            var testDatabase = builder.InitialCatalog;
            var masterBuilder = new SqlConnectionStringBuilder(connection) { InitialCatalog = "master" };
            await using (var master = new SqlConnection(masterBuilder.ConnectionString))
            {
                await master.OpenAsync();
                await using var command = master.CreateCommand();
                command.CommandText = $"ALTER DATABASE [{testDatabase}] SET MULTI_USER";
                await command.ExecuteNonQueryAsync();
            }
            var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" })
                .WithWebHostBuilder(host =>
                {
                    host.ConfigureLogging(logging => logging.ClearProviders());
                    host.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                        services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                        services.AddDbContext<SmartCvDbContext>(options =>
                        {
                            options.UseSqlServer(connection);
                            if (interceptor is not null) options.AddInterceptors(interceptor);
                        });
                        foreach (var descriptor in services.Where(s => s.ImplementationType == typeof(RunScoringWorker)
                            || s.ImplementationType == typeof(CvExtractionWorker)).ToList()) services.Remove(descriptor);
                    });
                });
            return new SqlEnvironment(connection, factory);
        }

        public SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);

        public async Task<(int Old, int New)> SeedPromptsAsync()
        {
            await using var db = Context();
            var old = NewPrompt("old");
            old.IsActive = true;
            var next = NewPrompt("new");
            db.AddRange(old, next);
            await db.SaveChangesAsync();
            return (old.PromptVersionId, next.PromptVersionId);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            await using var cleanup = Context();
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}

public sealed class PromptSqlFactAttribute : FactAttribute
{
    public PromptSqlFactAttribute()
    {
        var setting = Environment.GetEnvironmentVariable("SMARTCV_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(setting))
        {
            Skip = "NOT RUN: set SMARTCV_TEST_SQL_CONNECTION for a disposable SmartCv_A14_Test_ database.";
            return;
        }
        try
        {
            var builder = new SqlConnectionStringBuilder(setting);
            if (!builder.InitialCatalog.StartsWith("SmartCv_A14_Test_", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(builder.AttachDBFilename))
                Skip = "NOT RUN: connection must designate a SmartCv_A14_Test_ database without AttachDBFilename.";
        }
        catch (ArgumentException)
        {
            Skip = "NOT RUN: SQL test connection setting is invalid.";
        }
    }
}
