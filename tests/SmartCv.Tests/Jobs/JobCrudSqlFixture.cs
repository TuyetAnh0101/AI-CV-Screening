using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Jobs;

public sealed class A21SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A21_TEST_SQL_CONNECTION";
    public const string Prefix = "SmartCv_A21_Test_";
    public A21SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value)) { Skip = $"NOT RUN: set {Setting} for a new retained {Prefix} catalog."; return; }
        try { Validate(new SqlConnectionStringBuilder(value)); }
        catch (ArgumentException) { Skip = "NOT RUN: A21 requires its test catalog prefix and no attached files."; }
    }
    internal static void Validate(SqlConnectionStringBuilder value)
    {
        if (!value.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(value.AttachDBFilename))
            throw new ArgumentException("Only designated A21 test catalogs without attached files are allowed.");
    }
}

internal sealed record JobLegacySnapshot(Dictionary<string, int> Counts, string DataHash, string SchemaHash);

internal sealed class A21SqlEnvironment : IAsyncDisposable
{
    private readonly string connection;
    private readonly string catalog;
    private A21SqlEnvironment(string value, string name) { connection = value; catalog = name; }
    internal SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);

    internal static async Task<A21SqlEnvironment> CreateAsync(bool current = true)
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A21SqlFactAttribute.Setting));
        A21SqlFactAttribute.Validate(builder);
        builder.InitialCatalog = A21SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N");
        builder.Pooling = false; builder.ConnectTimeout = 10;
        Assert.Matches("^SmartCv_A21_Test_[0-9a-f]{32}$", builder.InitialCatalog);
        var env = new A21SqlEnvironment(builder.ConnectionString, builder.InitialCatalog);
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        await using (var sql = new SqlConnection(master.ConnectionString))
        {
            await sql.OpenAsync();
            await using var check = new SqlCommand("SELECT DB_ID(@catalog);", sql); check.Parameters.AddWithValue("@catalog", builder.InitialCatalog);
            Assert.Equal(DBNull.Value, await check.ExecuteScalarAsync());
            // Only this new GUID catalog: do not inherit the workstation model's SINGLE_USER mode.
            await using var create = new SqlCommand($"CREATE DATABASE [{builder.InitialCatalog}]; ALTER DATABASE [{builder.InitialCatalog}] SET MULTI_USER;", sql);
            await create.ExecuteNonQueryAsync();
        }
        await env.WriteAsync("catalog", new { Catalog = builder.InitialCatalog, Retained = true });
        await env.MigrateAsync(JobCrudMigrationTests.Previous);
        Assert.Equal(1, await env.IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Jobs') AND name=N'UQ_Jobs_Title' AND is_unique=1 AND is_unique_constraint=0;"));
        if (current) await env.MigrateAsync(JobCrudMigrationTests.Current);
        return env;
    }

    internal async Task<SqlConnection> OpenAsync()
    {
        var sql = new SqlConnection(connection);
        try
        {
            await sql.OpenAsync(); await using var check = new SqlCommand("SELECT DB_NAME();", sql);
            Assert.Equal(catalog, await check.ExecuteScalarAsync()); return sql;
        }
        catch { await sql.DisposeAsync(); throw; }
    }

    internal async Task MigrateAsync(string target)
    {
        Assert.Contains(target, new[] { JobCrudMigrationTests.Previous, JobCrudMigrationTests.Current });
        await using var guard = await OpenAsync(); await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    internal WebApplicationFactory<Program> Api(IInterceptor? interceptor = null, string environment = "Development", bool enabled = true, bool kestrel = false)
    {
        var factory = JobCrudEndpointTests.Factory(environment, enabled).WithWebHostBuilder(b =>
        {
            b.ConfigureLogging(l => l.ClearProviders());
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<SmartCvDbContext>>(); services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                services.AddDbContext<SmartCvDbContext>(options =>
                {
                    options.UseSqlServer(connection);
                    if (interceptor is not null) options.AddInterceptors(interceptor);
                });
            });
        });
        if (kestrel) factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        return factory;
    }

    internal async Task<Guid> SeedBusinessAsync()
    {
        await using var db = Context(); Assert.Equal(5, await db.Jobs.CountAsync());
        var run = new EvaluationRun { PromptVersion = new PromptVersion { VersionName = "A21 preservation", SystemPrompt = "synthetic" }, RunType = RunTypes.Screening, ModelName = "synthetic", KeywordWeight = .3m, AiWeight = .7m, Status = RunStatuses.Completed, TotalItems = 2, ProcessedItems = 2, CreatedAt = DateTime.UtcNow };
        var set = new EvaluationSet { Name = "A21 synthetic labels" };
        Guid firstCv = default;
        for (var i = 1; i <= 2; i++)
        {
            var cv = JobCrudEndpointTests.Cv(); if (i == 1) firstCv = cv.CvId;
            var app = new Application { Cv = cv, JobId = i, Stage = ApplicationStages.Interview, CreatedAt = DateTime.UtcNow };
            app.Events.Add(new ApplicationEvent { FromStage = ApplicationStages.New, ToStage = ApplicationStages.Interview, Actor = "Synthetic HR", CreatedAt = DateTime.UtcNow });
            var evaluation = new Evaluation { Application = app, Run = run, Status = EvaluationStatuses.Completed, KeywordScore = 70, AiScore = 80, FinalScore = 77, ScoreSource = ScoreSources.Combined, FitLabel = "Good", PredictedPass = true, Reasoning = "Synthetic evidence", CreatedAt = DateTime.UtcNow };
            evaluation.Skills.Add(new EvaluationSkill { JobId = i, SkillCode = "csharp", IsMatched = true, Evidence = "synthetic" });
            db.Add(evaluation); db.Add(new HrLabel { Application = app, EvaluationSet = set, IsPass = true, LabeledBy = "Synthetic HR", LabeledAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync(); return firstCv;
    }

    internal async Task AssertCurrentSchemaAsync()
    {
        Assert.Equal(2, await IntAsync("SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'dbo.Jobs') AND c.is_nullable=1 AND ((c.name=N'Headcount' AND t.name=N'int') OR (c.name=N'ClosingDate' AND t.name=N'date'));"));
        Assert.Equal(1, await IntAsync("SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.Jobs') AND name=N'CK_Jobs_Headcount';"));
        Assert.Equal(1, await IntAsync("SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Jobs') AND name=N'UQ_Jobs_Title' AND is_unique=1 AND is_unique_constraint=0;"));
        Assert.Equal(JobCrudMigrationTests.Current, (await HistoryAsync())[^1]);
    }

    internal async Task<int> IntAsync(string script)
    {
        await using var sql = await OpenAsync(); await using var command = new SqlCommand(script, sql);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    internal async Task<string[]> HistoryAsync()
    {
        await using var db = Context(); return (await db.Database.GetAppliedMigrationsAsync()).ToArray();
    }

    internal async Task ScriptAsync(string script)
    {
        Assert.DoesNotMatch(@"(?im)^\s*(?:USE\s|CREATE\s+DATABASE\s|DROP\s+DATABASE\s)", script);
        await using var sql = await OpenAsync();
        try
        {
            foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*$").Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                await using var command = new SqlCommand(batch, sql); await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await using var rollback = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", sql); await rollback.ExecuteNonQueryAsync();
        }
    }

    internal async Task<JobLegacySnapshot> SnapshotAsync(string label)
    {
        var tables = new (string Table, string Columns, string Key)[]
        {
            ("Jobs","JobId,Title,Description,MinGpa,MinYearsExperience,IsActive,CreatedAt","JobId"),
            ("Candidates","*","CandidateId"),("JobSkills","*","JobId,SkillCode"),("JobSkillAliases","*","JobId,SkillCode,Alias"),
            ("Cvs","*","CvId"),("Applications","*","ApplicationId"),("ApplicationEvents","*","EventId"),
            ("PromptVersions","*","PromptVersionId"),("EvaluationSets","*","EvaluationSetId"),("HrLabels","*","EvaluationSetId,ApplicationId"),
            ("EvaluationRuns","*","RunId"),("Evaluations","*","EvaluationId"),("EvaluationSkills","*","EvaluationId,JobId,SkillCode")
        };
        var data = new Dictionary<string, List<Dictionary<string, object?>>>();
        await using var sql = await OpenAsync();
        foreach (var (table, columns, key) in tables)
        {
            await using var command = new SqlCommand($"SELECT {columns} FROM dbo.[{table}] ORDER BY {key};", sql); // Fixed table/column allowlist, synthetic catalog only.
            await using var reader = await command.ExecuteReaderAsync(); var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
            data[table] = rows;
        }
        var json = JsonSerializer.Serialize(data); await WriteTextAsync(label + "-synthetic-data.json", json);
        await using var schemaCommand = new SqlCommand("""
            SELECT N'FK' AS Kind,f.name AS Name,OBJECT_NAME(f.parent_object_id) AS Parent,
                COL_NAME(c.parent_object_id,c.parent_column_id) AS ColumnName,OBJECT_NAME(f.referenced_object_id) AS Target,
                COL_NAME(c.referenced_object_id,c.referenced_column_id) AS TargetColumn,f.delete_referential_action_desc COLLATE DATABASE_DEFAULT AS Detail
            FROM sys.foreign_keys f JOIN sys.foreign_key_columns c ON c.constraint_object_id=f.object_id
            UNION ALL SELECT N'DEFAULT',d.name,OBJECT_NAME(d.parent_object_id),COL_NAME(d.parent_object_id,d.parent_column_id),N'',N'',d.definition FROM sys.default_constraints d
            UNION ALL SELECT N'CHECK',c.name,OBJECT_NAME(c.parent_object_id),N'',N'',N'',c.definition FROM sys.check_constraints c WHERE c.name<>N'CK_Jobs_Headcount'
            UNION ALL SELECT N'TRIGGER',t.name,OBJECT_NAME(t.parent_id),N'',N'',N'',OBJECT_DEFINITION(t.object_id) FROM sys.triggers t WHERE t.parent_class=1
            ORDER BY Kind,Name,ColumnName;
            """, sql);
        var schema = new StringBuilder();
        await using (var reader = await schemaCommand.ExecuteReaderAsync())
            while (await reader.ReadAsync()) for (var i = 0; i < reader.FieldCount; i++) schema.Append(JsonSerializer.Serialize(reader.GetValue(i))).Append('|');
        var snapshot = new JobLegacySnapshot(data.ToDictionary(p => p.Key, p => p.Value.Count), Hash(json), Hash(schema.ToString()));
        await WriteAsync(label + "-fingerprint", snapshot); return snapshot;
    }

    internal Task WriteAsync(string label, object value) => WriteTextAsync(label + ".json", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    internal Task WriteTextAsync(string label, string value)
    {
        var directory = TestSupport.RepoFile("artifacts/a21-sql"); Directory.CreateDirectory(directory);
        return File.WriteAllTextAsync(Path.Combine(directory, catalog + "-" + label), value);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Catalogs are retained; never DROP or delete CVs.
}
