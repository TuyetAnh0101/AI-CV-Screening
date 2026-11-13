using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Data.Migrations;
using SmartCv.Infrastructure.Privacy;
using Xunit.Abstractions;

namespace SmartCv.Tests;
public class A38RetentionSqlTests(ITestOutputHelper output)
{
    [Fact]
    public void Migration_Up_Down_and_snapshot_only_add_the_four_nullable_Cv_columns_constraint_and_filtered_index()
    {
        var migration = new CvConsentRetention();
        Assert.Equal(6, migration.UpOperations.Count);
        Assert.Equal(6, migration.DownOperations.Count);
        var columns = migration.UpOperations.OfType<AddColumnOperation>().OrderBy(c => c.Name).ToArray();
        Assert.Equal(new[] { "AnonymizedAt", "ConsentConfirmedAt", "RetainUntil", "SourceChannel" }, columns.Select(c => c.Name));
        Assert.All(columns, c =>
        {
            Assert.Equal("Cvs", c.Table);
            Assert.True(c.IsNullable);
            Assert.Equal(c.Name == "SourceChannel" ? "nvarchar(30)" : "datetime2", c.ColumnType);
        });
        var index = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>());
        Assert.Equal("IX_Cvs_RetainUntil", index.Name);
        Assert.Equal("Cvs", index.Table);
        Assert.Equal("[AnonymizedAt] IS NULL AND [RetainUntil] IS NOT NULL", index.Filter);
        var check = Assert.Single(migration.UpOperations.OfType<AddCheckConstraintOperation>());
        Assert.Equal("CK_Cvs_SourceChannel", check.Name);
        Assert.Equal("Cvs", check.Table);
        Assert.Equal(4, migration.DownOperations.OfType<DropColumnOperation>().Count());
        using var db = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=SmartCv_A38_Test_DesignOnly;Integrated Security=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [A38SqlFact]
    public async Task Migration_upgrades_legacy_data_without_loss_and_single_bulk_noop_reopen_preserve_retention()
    {
        await using var env = await Env.CreateAsync(legacy: true);
        await using (var db = env.Db())
        {
            Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync()).Count());
            var legacy = await db.Cvs.SingleAsync();
            Assert.Equal("A38-legacy.pdf", legacy.OriginalFileName);
            Assert.Null(legacy.SourceChannel);
            Assert.Null(legacy.ConsentConfirmedAt);
            Assert.Null(legacy.RetainUntil);
            Assert.Null(legacy.AnonymizedAt);
            foreach (var invalid in new[]
            {
                "Facebook",
                "email",
                "Email "
            }

            )
            {
                var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Cvs SET SourceChannel={invalid} WHERE CvId={legacy.CvId}"));
                Assert.Equal(547, error.Number);
            }
        }

        var ids = await env.SeedAsync(2, sameCv: true);
        var cvId = await env.CvId(ids[0]);
        using var first = await env.Http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", Request("Rejected", "MissingSkills"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        DateTime until;
        await using (var db = env.Db())
        {
            var ev = await db.ApplicationEvents.SingleAsync();
            until = DateTime.SpecifyKind(ev.CreatedAt, DateTimeKind.Utc).AddMonths(6);
            Assert.Equal(until, (await db.Cvs.SingleAsync(c => c.CvId == cvId)).RetainUntil);
        }

        using var noop = await env.Http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", Request("Rejected", "MissingSkills"));
        Assert.Equal("Unchanged", (await noop.Content.ReadFromJsonAsync<StageChangeResultDto>())!.Outcome);
        await using (var db = env.Db())
        {
            Assert.Equal(until, (await db.Cvs.SingleAsync(c => c.CvId == cvId)).RetainUntil);
            Assert.Single(await db.ApplicationEvents.ToListAsync());
        }

        using var bulk = await env.Http.PostAsJsonAsync("/api/applications/stage", new BulkChangeStageRequest { ApplicationIds = ids, ToStage = "OnHold", Actor = "A38 HR" });
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
        Assert.Equal(2, (await bulk.Content.ReadFromJsonAsync<BulkStageChangeResponse>())!.Changed);
        var later = DateTime.UtcNow.AddYears(3);
        await using (var db = env.Db())
        {
            (await db.Cvs.SingleAsync(c => c.CvId == cvId)).RetainUntil = later;
            await db.SaveChangesAsync();
        }

        using var reopen = await env.Http.PatchAsJsonAsync($"/api/applications/{ids[1]}/stage", Request("Interview"));
        Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
        using var rejectAgain = await env.Http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", Request("Rejected", "Duplicate"));
        Assert.Equal(HttpStatusCode.OK, rejectAgain.StatusCode);
        await using (var db = env.Db())
        {
            Assert.Equal(later, (await db.Cvs.SingleAsync(c => c.CvId == cvId)).RetainUntil);
            Assert.Equal(5, await db.ApplicationEvents.CountAsync());
        }

        output.WriteLine("Legacy migration preserved CV; case-sensitive CHECK rejected invalid source. Single/bulk/no-op/reopen preserve max retention.");
    }

    [A38SqlFact]
    public async Task Exported_SQL_applies_twice_without_data_loss_and_creates_only_the_expected_schema_additions()
    {
        await using var env = await Env.CreateAsync(legacy: true, useExportedSql: true);
        await using var db = env.Db();
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var legacy = await db.Cvs.SingleAsync();
        Assert.Equal("A38-legacy.pdf", legacy.OriginalFileName);
        Assert.Equal(new string ('A', 64), legacy.FileHash);
        Assert.Null(legacy.SourceChannel);
        Assert.Null(legacy.ConsentConfirmedAt);
        Assert.Null(legacy.RetainUntil);
        Assert.Null(legacy.AnonymizedAt);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Cvs') AND name=N'IX_Cvs_RetainUntil' AND has_filter=1").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.Cvs') AND name=N'CK_Cvs_SourceChannel' AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
    }

    [A38SqlFact]
    public async Task SQL_upload_blocks_missing_consent_and_persists_valid_metadata_without_rewriting_a_duplicate()
    {
        await using var env = await Env.CreateAsync();
        MultipartFormDataContent Form(bool consent, string channel = "Email")
        {
            var form = new MultipartFormDataContent();
            for (var i = 0; i < 2; i++)
                form.Add(new ByteArrayContent(TestSupport.Pdf("A38 SQL upload " + i)), "files", $"synthetic-{i}.pdf");
            form.Add(new StringContent(channel), "sourceChannel");
            if (consent)
                form.Add(new StringContent("true"), "consentConfirmed");
            return form;
        }

        using var invalid = Form(false);
        using var blocked = await env.Http.PostAsync("/api/cvs/upload", invalid);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        await using (var db = env.Db())
            Assert.Empty(await db.Cvs.ToListAsync());
        var before = DateTime.UtcNow;
        using var valid = Form(true);
        using var response = await env.Http.PostAsync("/api/cvs/upload", valid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CvUploadResponse>())!;
        Assert.Equal(2, result.SuccessCount);
        foreach (var file in result.Files)
        {
            var detail = (await env.Http.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{file.CvId}"))!;
            Assert.Equal("Email", detail.SourceChannel);
            Assert.InRange(detail.ConsentConfirmedAt!.Value, before, DateTime.UtcNow);
            Assert.Equal(DateTimeKind.Utc, detail.ConsentConfirmedAt.Value.Kind);
        }

        using var duplicate = Form(true, "Other");
        using var repeated = await env.Http.PostAsync("/api/cvs/upload", duplicate);
        var duplicateResult = (await repeated.Content.ReadFromJsonAsync<CvUploadResponse>())!;
        Assert.Equal(0, duplicateResult.SuccessCount);
        Assert.All(duplicateResult.Files, file => Assert.Equal("Duplicate", file.ErrorCode));
        await using (var db = env.Db())
        {
            Assert.Equal(2, await db.Cvs.CountAsync());
            Assert.All(await db.Cvs.ToListAsync(), cv => Assert.Equal("Email", cv.SourceChannel));
        }
    }

    private sealed class UpdateStarted(TaskCompletionSource started) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                started.TrySetResult();
            return base.NonQueryExecutingAsync(command, eventData, result, ct);
        }
    }

    [A38SqlFact]
    public async Task Concurrent_CV_updates_from_different_jobs_cannot_replace_later_deadline_with_earlier_value()
    {
        await using var env = await Env.CreateAsync();
        var ids = await env.SeedAsync(2, true);
        var cvId = await env.CvId(ids[0]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var laterDb = env.Db();
        await using var earlierDb = env.Db(new UpdateStarted(started));
        Assert.Null((await laterDb.Cvs.SingleAsync()).RetainUntil);
        Assert.Null((await earlierDb.Cvs.SingleAsync()).RetainUntil);
        var late = new DateTime(2028, 8, 31, 3, 0, 0, DateTimeKind.Utc);
        var early = new DateTime(2026, 8, 31, 3, 0, 0, DateTimeKind.Utc);
        await using var laterTx = await laterDb.Database.BeginTransactionAsync();
        await CvRetentionWriter.ExtendAsync(laterDb, [cvId], "Rejected", late, 6, default);
        async Task Earlier()
        {
            await using var tx = await earlierDb.Database.BeginTransactionAsync();
            await CvRetentionWriter.ExtendAsync(earlierDb, [cvId], "OnHold", early, 6, default);
            await tx.CommitAsync();
        }

        var pending = Earlier();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await laterTx.CommitAsync();
        await pending;
        await using var verify = env.Db();
        Assert.Equal(late.AddMonths(6), (await verify.Cvs.SingleAsync()).RetainUntil);
        output.WriteLine("Two stale CV reads + overlapping SQL updates: earlier deadline did not overwrite the later deadline.");
    }

    private sealed class SaveRace : SaveChangesInterceptor
    {
        public Func<Task>? BeforeSave;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (BeforeSave is { } action)
            {
                BeforeSave = null;
                await action();
            }

            return result;
        }
    }

    private sealed class FailAfterDecisionWrite : DbCommandInterceptor
    {
        public bool Armed;
        public int Writes;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (Armed && (command.CommandText.Contains("UPDATE [Applications]") || command.CommandText.Contains("INSERT INTO [ApplicationEvents]")))
            {
                Armed = false;
                Writes++;
                throw new InvalidOperationException("Synthetic A38 fault after a successful decision write");
            }

            return base.ReaderExecutedAsync(command, eventData, result, ct);
        }
    }

    [A38SqlFactTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failure_or_save_time_concurrency_rolls_back_CV_deadline_stage_and_our_history(bool bulk, bool conflict)
    {
        var race = new SaveRace();
        var fault = new FailAfterDecisionWrite();
        await using var env = await Env.CreateAsync(interceptors: [race, fault]);
        var ids = await env.SeedAsync(bulk ? 2 : 1);
        Dictionary<int, Guid> versions;
        await using (var db = env.Db())
            versions = await db.Applications.ToDictionaryAsync(a => a.ApplicationId, a => a.StageVersion);
        if (conflict)
            race.BeforeSave = async () =>
            {
                await using var other = env.Db();
                var app = await other.Applications.SingleAsync(a => a.ApplicationId == ids.Last());
                app.Stage = "Reviewing";
                app.StageUpdatedAt = DateTime.UtcNow;
                other.ApplicationEvents.Add(new() { ApplicationId = app.ApplicationId, FromStage = "New", ToStage = "Reviewing", Actor = "Other HR", CreatedAt = DateTime.UtcNow });
                await other.SaveChangesAsync();
            };
        else
            fault.Armed = true;
        using var response = bulk ? await env.Http.PostAsJsonAsync("/api/applications/stage", new BulkChangeStageRequest { ApplicationIds = ids, ToStage = "Rejected", RejectReason = "MissingSkills", Actor = "A38 HR", ExpectedStageVersions = versions }) : await env.Http.PatchAsJsonAsync($"/api/applications/{ids[0]}/stage", new ChangeStageRequest { ToStage = "Rejected", RejectReason = "MissingSkills", Actor = "A38 HR", ExpectedStageVersion = versions[ids[0]] });
        Assert.Equal(conflict ? HttpStatusCode.Conflict : HttpStatusCode.InternalServerError, response.StatusCode);
        await using var verify = env.Db();
        Assert.All(await verify.Cvs.ToListAsync(), c => Assert.Null(c.RetainUntil));
        var apps = await verify.Applications.OrderBy(a => a.ApplicationId).ToListAsync();
        if (conflict)
        {
            Assert.Equal("Reviewing", apps.Last().Stage);
            Assert.NotEqual(versions[ids.Last()], apps.Last().StageVersion);
            Assert.Equal("Other HR", (await verify.ApplicationEvents.SingleAsync()).Actor);
            Assert.All(apps.Take(apps.Count - 1), a => Assert.Equal("New", a.Stage));
        }
        else
        {
            Assert.All(apps, a => Assert.Equal("New", a.Stage));
            Assert.Empty(await verify.ApplicationEvents.ToListAsync());
            Assert.Equal(1, fault.Writes);
        }

        output.WriteLine($"bulk={bulk}, conflict={conflict}: retention rolled back; no false HR history; HTTP {(int)response.StatusCode}.");
    }

    private static ChangeStageRequest Request(string stage, string? reason = null) => new()
    {
        ToStage = stage,
        RejectReason = reason,
        Actor = "A38 HR"
    };
    private sealed class Env : IAsyncDisposable
    {
        private readonly string connection;
        private WebApplicationFactory<Program>? factory;
        public HttpClient Http { get; private set; } = null!;

        private Env(string value) => connection = value;
        public SmartCvDbContext Db(IInterceptor? interceptor = null)
        {
            var b = new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection);
            if (interceptor is not null)
                b.AddInterceptors(interceptor);
            return new(b.Options);
        }

        public static async Task<Env> CreateAsync(bool legacy = false, IInterceptor[]? interceptors = null, bool useExportedSql = false)
        {
            var sql = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A38SqlFactAttribute.Setting));
            A38SqlFactAttribute.Validate(sql);
            sql.InitialCatalog = "SmartCv_A38_Test_" + Guid.NewGuid().ToString("N");
            sql.Pooling = false;
            sql.ConnectTimeout = 10;
            var evidence = TestSupport.RepoFile("artifacts/a38-tests/retained-sql-databases.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            await File.AppendAllTextAsync(evidence, sql.InitialCatalog + Environment.NewLine);
            var env = new Env(sql.ConnectionString);
            await using (var db = env.Db())
            {
                if (legacy)
                {
                    await db.GetService<IMigrator>().MigrateAsync("20261112045258_ApplicationStages");
                    await db.Database.ExecuteSqlRawAsync("INSERT INTO Cvs(CvId,OriginalFileName,StoredPath,ContentType,FileSizeBytes,FileHash,ExtractionStatus) VALUES(NEWID(),N'A38-legacy.pdf',N'test-only',N'application/pdf',1,REPLICATE('A',64),N'Completed')");
                }

                if (useExportedSql)
                {
                    var sqlFile = await File.ReadAllTextAsync(TestSupport.RepoFile("database/a38_cv_consent_retention.sql"));
                    var batches = Regex.Split(sqlFile, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    for (var attempt = 0; attempt < 2; attempt++)
                        foreach (var batch in batches.Where(b => !string.IsNullOrWhiteSpace(b)))
                            await db.Database.ExecuteSqlRawAsync(batch);
                }
                else
                    await db.Database.MigrateAsync();
            }

            env.factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" }).WithWebHostBuilder(b =>
            {
                b.ConfigureLogging(l => l.ClearProviders());
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                    s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                    s.AddDbContext<SmartCvDbContext>(o =>
                    {
                        o.UseSqlServer(env.connection, c => c.MaxBatchSize(1));
                        if (interceptors is not null)
                            o.AddInterceptors(interceptors);
                    });
                });
            });
            env.Http = env.factory.CreateClient();
            return env;
        }

        public async Task<List<int>> SeedAsync(int count, bool sameCv = false)
        {
            await using var db = Db();
            var shared = A38ConsentEndpointTests.LegacyCv();
            var apps = Enumerable.Range(0, count).Select(i => new Application { JobId = sameCv ? i + 1 : 1, Cv = sameCv ? shared : A38ConsentEndpointTests.LegacyCv() }).ToList();
            db.AddRange(apps);
            await db.SaveChangesAsync();
            return apps.Select(a => a.ApplicationId).ToList();
        }

        public async Task<Guid> CvId(int applicationId)
        {
            await using var db = Db();
            return await db.Applications.Where(a => a.ApplicationId == applicationId).Select(a => a.CvId).SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (factory is not null)
                await factory.DisposeAsync();
        } // retain test catalog, never DROP
    }
}

public sealed class A38SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A38_TEST_SQL_CONNECTION";
    public A38SqlFactAttribute()
    {
        if (!Enabled())
            Skip = $"Set {Setting} for a new retained A38 SQL test database.";
    }

    internal static bool Enabled()
    {
        var s = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(s))
            return false;
        Validate(new SqlConnectionStringBuilder(s));
        return true;
    }

    internal static void Validate(SqlConnectionStringBuilder s)
    {
        if (!s.InitialCatalog.StartsWith("SmartCv_A38_Test_", StringComparison.Ordinal) || !string.IsNullOrEmpty(s.AttachDBFilename))
            throw new ArgumentException("Only a SmartCv_A38_Test_ catalog without AttachDBFilename is allowed.");
    }
}

public sealed class A38SqlFactTheoryAttribute : TheoryAttribute
{
    public A38SqlFactTheoryAttribute()
    {
        if (!A38SqlFactAttribute.Enabled())
            Skip = $"Set {A38SqlFactAttribute.Setting} for isolated SQL tests.";
    }
}
