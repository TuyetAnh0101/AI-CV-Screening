using System.Collections.Concurrent;
using System.Data.Common;
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

namespace SmartCv.Tests.Runs;

public sealed class A25SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A25_TEST_SQL_CONNECTION";
    public const string Prefix = "SmartCv_A25_Test_";
    public A25SqlFactAttribute()
    {
        var setting = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(setting)) { Skip = $"NOT RUN: set {Setting} for a new retained GUID test catalog."; return; }
        try { Validate(new SqlConnectionStringBuilder(setting)); }
        catch (ArgumentException) { Skip = "NOT RUN: A25 requires its test prefix and forbids attached files."; }
    }
    internal static void Validate(SqlConnectionStringBuilder builder)
    {
        if (!builder.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(builder.AttachDBFilename))
            throw new ArgumentException("A25 SQL only accepts its own test prefix without attached files.");
    }
}

internal sealed class A25SqlEnvironment : IAsyncDisposable
{
    private readonly string connection;
    internal string Catalog { get; }
    private A25SqlEnvironment(string value, string catalog) { connection = value; Catalog = catalog; }
    internal SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);

    internal static async Task<A25SqlEnvironment> CreateAsync()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A25SqlFactAttribute.Setting));
        A25SqlFactAttribute.Validate(builder);
        builder.InitialCatalog = A25SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N"); builder.Pooling = false; builder.ConnectTimeout = 10;
        Assert.Matches("^SmartCv_A25_Test_[0-9a-f]{32}$", builder.InitialCatalog);
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        await using (var sql = new SqlConnection(master.ConnectionString))
        {
            await sql.OpenAsync();
            await using var check = new SqlCommand("SELECT DB_ID(@name);", sql); check.Parameters.AddWithValue("@name", builder.InitialCatalog);
            Assert.Equal(DBNull.Value, await check.ExecuteScalarAsync());
            // Create only the proven-absent GUID catalog; never change model/master or an existing catalog.
            await using var create = new SqlCommand($"CREATE DATABASE [{builder.InitialCatalog}]; ALTER DATABASE [{builder.InitialCatalog}] SET MULTI_USER;", sql);
            await create.ExecuteNonQueryAsync();
        }
        var env = new A25SqlEnvironment(builder.ConnectionString, builder.InitialCatalog);
        await using var db = env.Context(); await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray(); Assert.Equal(6, migrations.Length);
        Assert.EndsWith("JobHeadcountClosingDate", migrations[^1]);
        await env.WriteAsync("catalog", new { env.Catalog, Retained = true, Migrations = migrations }); return env;
    }

    internal WebApplicationFactory<Program> Api(IInterceptor? interceptor = null, bool kestrel = false)
    {
        var factory = RunHistoryEndpointTests.Factory().WithWebHostBuilder(b =>
        {
            b.ConfigureLogging(l => l.ClearProviders());
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<DbContextOptions<SmartCvDbContext>>(); s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                s.AddDbContext<SmartCvDbContext>(o => { o.UseSqlServer(connection); if (interceptor is not null) o.AddInterceptors(interceptor); });
            });
        });
        if (kestrel) factory.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        return factory;
    }

    internal async Task<SqlConnection> OpenAsync()
    {
        var sql = new SqlConnection(connection);
        try { await sql.OpenAsync(); await using var guard = new SqlCommand("SELECT DB_NAME();", sql); Assert.Equal(Catalog, await guard.ExecuteScalarAsync()); return sql; }
        catch { await sql.DisposeAsync(); throw; }
    }

    internal async Task<string> FingerprintAsync(string label)
    {
        var tables = new (string Table, string Key)[]
        {
            ("Jobs","JobId"),("Candidates","CandidateId"),("Cvs","CvId"),("Applications","ApplicationId"),("ApplicationEvents","EventId"),
            ("JobSkills","JobId,SkillCode"),("JobSkillAliases","JobId,SkillCode,Alias"),("PromptVersions","PromptVersionId"),
            ("EvaluationSets","EvaluationSetId"),("HrLabels","EvaluationSetId,ApplicationId"),("EvaluationRuns","RunId"),
            ("Evaluations","EvaluationId"),("EvaluationSkills","EvaluationId,JobId,SkillCode")
        };
        var data = new Dictionary<string, List<object?[]>>(); await using var sql = await OpenAsync();
        foreach (var (table, key) in tables)
        {
            // Fixed identifiers; synthetic catalog only. Store hashes/counts, never raw rows in artifacts.
            await using var command = new SqlCommand($"SELECT * FROM dbo.[{table}] ORDER BY {key};", sql);
            await using var reader = await command.ExecuteReaderAsync(); var rows = new List<object?[]>();
            while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
            data[table] = rows;
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))));
        await WriteAsync(label, new { Hash = hash, Counts = data.ToDictionary(p => p.Key, p => p.Value.Count) }); return hash;
    }

    internal Task WriteAsync(string label, object value)
    {
        var directory = TestSupport.RepoFile("artifacts/a25-sql"); Directory.CreateDirectory(directory);
        return File.WriteAllTextAsync(Path.Combine(directory, Catalog + "-" + label + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Retain every test catalog and synthetic CV.
}

internal sealed class HistoryReadRecorder : DbCommandInterceptor
{
    internal bool Active { get; set; }
    internal bool Delay { get; set; }
    internal bool CancellationObserved { get; private set; }
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ConcurrentQueue<string> Commands { get; } = new();
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (Active)
        {
            Commands.Enqueue(command.CommandText); Assert.StartsWith("SELECT", command.CommandText.TrimStart(), StringComparison.OrdinalIgnoreCase);
            if (Delay)
            {
                Started.TrySetResult();
                try { await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken); }
                catch (OperationCanceledException) { CancellationObserved = cancellationToken.IsCancellationRequested; throw; }
            }
        }
        return result;
    }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Active) throw new InvalidOperationException("A25 reads must not execute EF non-query commands.");
        return ValueTask.FromResult(result);
    }
}

internal sealed class NativeHistoryCancellation : DbCommandInterceptor
{
    internal TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool CancellationObserved { get; private set; }
    internal int Attempts { get; private set; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Attempts++;
        cancellationToken.Register(() => CancellationObserved = true);
        command.CommandText = "WAITFOR DELAY '00:00:10'; " + command.CommandText;
        Started.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);
        return ValueTask.FromResult(result);
    }
}
