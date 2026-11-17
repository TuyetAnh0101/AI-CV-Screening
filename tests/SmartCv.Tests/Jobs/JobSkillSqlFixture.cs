using System.Net;
using System.Security.Cryptography;
using System.Text;
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
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Jobs;

public sealed class A22SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A22_TEST_SQL_CONNECTION";
    public const string Prefix = "SmartCv_A22_Test_";
    public A22SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value)) { Skip = $"NOT RUN: set {Setting} for a new retained GUID catalog."; return; }
        try { Validate(new SqlConnectionStringBuilder(value)); }
        catch (ArgumentException) { Skip = "NOT RUN: A22 requires a test prefix and forbids attached database files."; }
    }
    internal static void Validate(SqlConnectionStringBuilder value)
    {
        if (!value.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(value.AttachDBFilename))
            throw new ArgumentException("Only designated A22 test catalogs without attached files are allowed.");
    }
}

internal sealed class A22SqlEnvironment : IAsyncDisposable
{
    private readonly string connection;
    internal string Catalog { get; }
    private A22SqlEnvironment(string value, string catalog) { connection = value; Catalog = catalog; }
    internal SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);

    internal static async Task<A22SqlEnvironment> CreateAsync()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A22SqlFactAttribute.Setting));
        A22SqlFactAttribute.Validate(builder);
        builder.InitialCatalog = A22SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N");
        builder.Pooling = false; builder.ConnectTimeout = 10;
        Assert.Matches("^SmartCv_A22_Test_[0-9a-f]{32}$", builder.InitialCatalog);
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        await using (var sql = new SqlConnection(master.ConnectionString))
        {
            await sql.OpenAsync();
            await using var check = new SqlCommand("SELECT DB_ID(@catalog);", sql); check.Parameters.AddWithValue("@catalog", builder.InitialCatalog);
            Assert.Equal(DBNull.Value, await check.ExecuteScalarAsync());
            // A GUID catalog proven absent above. Never reuse, drop or detach an existing catalog.
            await using var create = new SqlCommand($"CREATE DATABASE [{builder.InitialCatalog}]; ALTER DATABASE [{builder.InitialCatalog}] SET MULTI_USER;", sql);
            await create.ExecuteNonQueryAsync();
        }
        var env = new A22SqlEnvironment(builder.ConnectionString, builder.InitialCatalog);
        await using var db = env.Context(); await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(JobCrudMigrationTests.Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(5, await db.Jobs.CountAsync()); Assert.Equal(29, await db.JobSkills.CountAsync()); Assert.Equal(52, await db.JobSkillAliases.CountAsync());
        await env.WriteAsync("catalog", new { env.Catalog, Retained = true, Migration = JobCrudMigrationTests.Current });
        return env;
    }

    internal WebApplicationFactory<Program> Api(IInterceptor? interceptor = null, bool kestrel = false, bool singleBatch = false)
    {
        var factory = JobCrudEndpointTests.Factory().WithWebHostBuilder(b =>
        {
            b.ConfigureLogging(l => l.ClearProviders());
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<DbContextOptions<SmartCvDbContext>>(); s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                s.AddDbContext<SmartCvDbContext>(o =>
                {
                    o.UseSqlServer(connection, sql => { if (singleBatch) sql.MaxBatchSize(1); });
                    if (interceptor is not null) o.AddInterceptors(interceptor);
                });
            });
        });
        if (kestrel) factory.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        return factory;
    }

    internal async Task<SqlConnection> OpenAsync()
    {
        var sql = new SqlConnection(connection);
        try
        {
            await sql.OpenAsync(); await using var guard = new SqlCommand("SELECT DB_NAME();", sql);
            Assert.Equal(Catalog, await guard.ExecuteScalarAsync()); return sql;
        }
        catch { await sql.DisposeAsync(); throw; }
    }

    internal async Task<int> IntAsync(string script)
    {
        await using var sql = await OpenAsync(); await using var command = new SqlCommand(script, sql);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    internal async Task<string> HistoryFingerprintAsync(string label)
    {
        var tables = new (string Table, string Key)[]
        {
            ("Cvs","CvId"),("Applications","ApplicationId"),("ApplicationEvents","EventId"),("PromptVersions","PromptVersionId"),
            ("EvaluationRuns","RunId"),("Evaluations","EvaluationId"),("EvaluationSkills","EvaluationId,JobId,SkillCode"),("HrLabels","EvaluationSetId,ApplicationId")
        };
        var data = new Dictionary<string, List<object?[]>>();
        await using var sql = await OpenAsync();
        foreach (var (table, key) in tables)
        {
            // Fixed table/key allowlist in the new synthetic catalog only.
            await using var command = new SqlCommand($"SELECT * FROM dbo.[{table}] ORDER BY {key};", sql);
            await using var reader = await command.ExecuteReaderAsync(); var rows = new List<object?[]>();
            while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
            data[table] = rows;
        }
        var json = JsonSerializer.Serialize(data);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await WriteAsync(label, new { Hash = hash, Counts = data.ToDictionary(p => p.Key, p => p.Value.Count) });
        return hash;
    }

    internal Task WriteAsync(string label, object value)
    {
        var directory = TestSupport.RepoFile("artifacts/a22-sql"); Directory.CreateDirectory(directory);
        return File.WriteAllTextAsync(Path.Combine(directory, Catalog + "-" + label + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Intentionally retain every catalog and synthetic CV.
}
