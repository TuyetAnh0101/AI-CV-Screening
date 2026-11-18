using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
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
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Jobs;
using SmartCv.Tests.Runs;

namespace SmartCv.Tests;

/// <summary>Opt-in: set SMARTCV_A23_TEST_SQL_CONNECTION with Initial Catalog starting "SmartCv_A23_Test_".</summary>
public sealed class A23SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A23_TEST_SQL_CONNECTION";
    public const string Prefix = "SmartCv_A23_Test_";
    public A23SqlFactAttribute()
    {
        var setting = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(setting)) { Skip = $"NOT RUN: set {Setting} for a new retained GUID test catalog."; return; }
        try { Validate(new SqlConnectionStringBuilder(setting)); }
        catch (ArgumentException) { Skip = "NOT RUN: A23 requires its test prefix and forbids attached files."; }
    }
    internal static void Validate(SqlConnectionStringBuilder builder)
    {
        if (!builder.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(builder.AttachDBFilename))
            throw new ArgumentException("A23 SQL only accepts its own test prefix without attached files.");
    }
}

/// <summary>A brand-new GUID catalog per test; never touches another database. Catalogs are retained for review.</summary>
internal sealed class A23SqlEnvironment
{
    private readonly string connection;
    internal string Catalog { get; }
    private A23SqlEnvironment(string value, string catalog) { connection = value; Catalog = catalog; }
    internal SmartCvDbContext Context() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);

    internal static async Task<A23SqlEnvironment> CreateAsync()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A23SqlFactAttribute.Setting));
        A23SqlFactAttribute.Validate(builder);
        builder.InitialCatalog = A23SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N"); builder.Pooling = false; builder.ConnectTimeout = 10;
        Assert.Matches("^SmartCv_A23_Test_[0-9a-f]{32}$", builder.InitialCatalog);
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
        var env = new A23SqlEnvironment(builder.ConnectionString, builder.InitialCatalog);
        await using var db = env.Context(); await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        await env.WriteAsync("catalog", new { env.Catalog, Retained = true, Migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray() });
        return env;
    }

    internal WebApplicationFactory<Program> Api(IInterceptor? interceptor = null) => RunHistoryEndpointTests.Factory().WithWebHostBuilder(b =>
    {
        b.ConfigureLogging(l => l.ClearProviders());
        b.ConfigureTestServices(s =>
        {
            s.RemoveAll<DbContextOptions<SmartCvDbContext>>(); s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            s.AddDbContext<SmartCvDbContext>(o => { o.UseSqlServer(connection); if (interceptor is not null) o.AddInterceptors(interceptor); });
        });
    });

    internal async Task<SqlConnection> OpenAsync()
    {
        var sql = new SqlConnection(connection);
        try { await sql.OpenAsync(); await using var guard = new SqlCommand("SELECT DB_NAME();", sql); Assert.Equal(Catalog, await guard.ExecuteScalarAsync()); return sql; }
        catch { await sql.DisposeAsync(); throw; }
    }

    /// <summary>Runs a repository SQL script (single batch) in THIS catalog and returns every result set.</summary>
    internal async Task<List<List<Dictionary<string, object?>>>> ScriptAsync(string relative, Func<string, string>? edit = null, params SqlParameter[] parameters)
    {
        var text = await File.ReadAllTextAsync(TestSupport.RepoFile(relative));
        Assert.DoesNotContain("\nGO", text, StringComparison.OrdinalIgnoreCase);
        if (edit is not null) text = edit(text);
        await using var sql = await OpenAsync();
        await using var command = new SqlCommand(text, sql) { CommandTimeout = 300 };
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var sets = new List<List<Dictionary<string, object?>>>();
        do
        {
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
            sets.Add(rows);
        } while (await reader.NextResultAsync());
        return sets;
    }

    internal Task WriteAsync(string label, object value)
    {
        var directory = TestSupport.RepoFile("artifacts/a23-sql"); Directory.CreateDirectory(directory);
        return File.WriteAllTextAsync(Path.Combine(directory, Catalog + "-" + label + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Records EF commands while active; any EF non-query during a GET fails the test.</summary>
internal sealed class A23ReadRecorder : DbCommandInterceptor
{
    internal bool Active { get; set; }
    internal ConcurrentQueue<string> Commands { get; } = new();
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (Active) Commands.Enqueue(command.CommandText);
        return ValueTask.FromResult(result);
    }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Active) throw new InvalidOperationException("A23 GET /api/cvs must not execute EF non-query commands.");
        return ValueTask.FromResult(result);
    }
}

public class A23CvListSqlServerTests
{
    private static async Task<JsonElement> GetAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("/api/cvs?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Files(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fileName").GetString()!).Order(StringComparer.Ordinal).ToArray();

    [A23SqlFact]
    public async Task SQL_search_follows_the_column_collation_and_treats_wildcards_literally()
    {
        var env = await A23SqlEnvironment.CreateAsync();
        await using (var db = env.Context())
        {
            Cv Make(string file, string text, Candidate? candidate = null, bool anonymized = false)
            {
                var cv = JobCrudEndpointTests.Cv(); cv.OriginalFileName = file; cv.ExtractedText = text; cv.Candidate = candidate;
                cv.AnonymizedAt = anonymized ? DateTime.UtcNow : null; cv.UploadedAt = DateTime.UtcNow; return cv;
            }
            db.Cvs.AddRange(
                Make("an.pdf", "Kỹ năng React, SQL Server", new Candidate { FullName = "Nguyễn Văn An", Email = "an@example.com", CreatedAt = DateTime.UtcNow }),
                Make("b.pdf", "Giảm 500 lỗi mỗi tháng"),
                Make("c.pdf", "Tăng 50% hiệu suất"),
                Make("d.pdf", "Dùng CX framework"),
                Make("e.pdf", "Ký hiệu C_ trong tài liệu"),
                Make("g.pdf", "Biểu thức [ab]c"),
                Make("h.pdf", "Mảng [ab] và react, Nguyễn", anonymized: true));
            await db.SaveChangesAsync();
        }
        var compare = await env.ScriptAsync("database/a23_so_sanh_tim_kiem.sql",
            text => Replace(text, "DECLARE @Search NVARCHAR(100) = N'React';", "DECLARE @Search NVARCHAR(100) = @SearchValue;"),
            new SqlParameter("@SearchValue", "React"));
        var collation = (string)compare[1].Single()["collation_name"]!;
        bool ci = collation.Contains("_CI", StringComparison.Ordinal), ai = collation.Contains("_AI", StringComparison.Ordinal);

        using var factory = env.Api(); using var client = factory.CreateClient();
        var react = await GetAsync(client, "search=react");
        Assert.Equal(ci ? ["an.pdf"] : Array.Empty<string>(), Files(react));                  // anonymized h.pdf never found
        Assert.Equal(Convert.ToInt32(compare[0].Single()["SoCvKhop"]), (await GetAsync(client, "search=React")).GetProperty("totalCount").GetInt32());
        Assert.Equal(["an.pdf"], Files(await GetAsync(client, "search=" + Uri.EscapeDataString("Nguyễn Văn"))));
        Assert.Equal(ai ? ["an.pdf"] : Array.Empty<string>(), Files(await GetAsync(client, "search=Nguyen")));
        // Wildcards are data: "50%" must not match "500", "C_" must not match "CX", "[ab]" is not a character class.
        Assert.Equal(["c.pdf"], Files(await GetAsync(client, "search=" + Uri.EscapeDataString("50%"))));
        Assert.Equal(["e.pdf"], Files(await GetAsync(client, "search=" + Uri.EscapeDataString("C_"))));
        Assert.Equal(["g.pdf"], Files(await GetAsync(client, "search=" + Uri.EscapeDataString("[ab]"))));
        await env.WriteAsync("collation", new { Column = "Cvs.ExtractedText", collation, CaseInsensitive = ci, AccentInsensitive = ai });
    }

    [A23SqlFact]
    public async Task SQL_listing_only_reads_and_uses_a_constant_number_of_queries_per_page()
    {
        var env = await A23SqlEnvironment.CreateAsync();
        int jobId;
        await using (var db = env.Context())
        {
            jobId = await db.Jobs.OrderBy(j => j.JobId).Select(j => j.JobId).FirstAsync();
            var prompt = new PromptVersion { VersionName = "A23 sql", SystemPrompt = "synthetic", IsActive = true };
            var run = new EvaluationRun { RunType = RunTypes.Screening, Status = RunStatuses.Completed, PromptVersion = prompt, ModelName = "fixture",
                Temperature = .2m, KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70, CreatedAt = DateTime.UtcNow };
            for (var i = 0; i < 120; i++)
            {
                var cv = JobCrudEndpointTests.Cv(); cv.OriginalFileName = $"sql-{i:000}.pdf"; cv.UploadedAt = DateTime.UtcNow.AddMinutes(-i);
                cv.Candidate = new Candidate { FullName = $"Ứng viên {i:000}", Email = $"sql{i:000}@example.com", CreatedAt = DateTime.UtcNow };
                var app = new Application { Cv = cv, JobId = jobId, Stage = i % 2 == 0 ? ApplicationStages.New : ApplicationStages.Rejected,
                    RejectReason = i % 2 == 0 ? null : RejectReasons.MissingSkills };
                run.Evaluations.Add(new Evaluation { Application = app, Status = EvaluationStatuses.Completed, FinalScore = i % 100, CreatedAt = DateTime.UtcNow });
                db.Add(app);
            }
            db.Add(run); await db.SaveChangesAsync();
        }
        var recorder = new A23ReadRecorder();
        using var factory = env.Api(recorder); using var client = factory.CreateClient();
        var observed = new Dictionary<string, object>();
        async Task<(int Commands, int Scans, JsonElement Page)> Measure(string query)
        {
            recorder.Commands.Clear(); recorder.Active = true;
            var page = await GetAsync(client, query);
            recorder.Active = false;
            var commands = recorder.Commands.ToArray();
            Assert.All(commands, c => Assert.StartsWith("SELECT", c.TrimStart(), StringComparison.OrdinalIgnoreCase));
            var scans = commands.Count(c => c.Contains(" LIKE ", StringComparison.OrdinalIgnoreCase));
            observed[query] = new { Commands = commands.Length, LikeScans = scans, Total = page.GetProperty("totalCount").GetInt32() };
            return (commands.Length, scans, page);
        }
        // Full page: ids + count + details + split applications. Same for 1 or 100 rows: no N+1.
        var one = await Measure("search=sql&pageSize=1"); var hundred = await Measure("search=sql&pageSize=100");
        Assert.Equal((4, 2), (one.Commands, one.Scans)); Assert.Equal((4, 2), (hundred.Commands, hundred.Scans));
        Assert.Equal(120, one.Page.GetProperty("totalCount").GetInt32()); Assert.Equal(120, hundred.Page.GetProperty("totalCount").GetInt32());
        // Partial (last) page: the page proves the total, so no second scan.
        var rejected = await Measure($"search=sql&jobId={jobId}&stage=Rejected&pageSize=100");
        Assert.Equal((3, 1), (rejected.Commands, rejected.Scans)); Assert.Equal(60, rejected.Page.GetProperty("totalCount").GetInt32());
        Assert.All(rejected.Page.GetProperty("items").EnumerateArray(), i => Assert.Equal(ApplicationStages.Rejected, i.GetProperty("applications")[0].GetProperty("stage").GetString()));
        var second = await Measure("search=sql&page=2&pageSize=100");
        Assert.Equal((3, 1), (second.Commands, second.Scans)); Assert.Equal((120, 20), (second.Page.GetProperty("totalCount").GetInt32(), second.Page.GetProperty("items").GetArrayLength()));
        // No match (the guide's slowest case): exactly one scan, nothing else.
        var none = await Measure("search=khongcotrongcv");
        Assert.Equal((1, 1), (none.Commands, none.Scans)); Assert.Equal(0, none.Page.GetProperty("totalCount").GetInt32());
        // Empty page beyond the end still reports the real total.
        var beyond = await Measure("search=sql&page=3&pageSize=100");
        Assert.Equal((2, 2), (beyond.Commands, beyond.Scans)); Assert.Equal(120, beyond.Page.GetProperty("totalCount").GetInt32());
        await env.WriteAsync("query-count", observed);
    }

    [A23SqlFact]
    public async Task SQL_ZIP_scripts_seed_500_CVs_and_every_search_meets_the_two_second_target()
    {
        var env = await A23SqlEnvironment.CreateAsync();
        int jobsBefore; await using (var db = env.Context()) jobsBefore = await db.Jobs.CountAsync();
        var seeded = (await env.ScriptAsync("database/a23_tao_du_lieu_hieu_nang.sql"))[^1].Single();
        Assert.Equal(500, Convert.ToInt32(seeded["SoCvGia"]));
        int jobId; await using (var db = env.Context()) jobId = await db.Jobs.Where(j => j.IsActive).OrderBy(j => j.JobId).Select(j => j.JobId).FirstAsync();

        using var factory = env.Api(); using var client = factory.CreateClient();
        var queries = new[] { "search=React", "search=SQL%20Server", "search=perf_00250", "search=khongcotrongcv", $"jobId={jobId}&stage=Rejected&page=5" };
        var results = new List<object>();
        foreach (var query in queries)
        {
            JsonElement page = default; long wall = 0;
            for (var run = 0; run < 3; run++)   // same as the guide: keep the last of three runs (warm cache)
            {
                var watch = Stopwatch.StartNew(); page = await GetAsync(client, query); watch.Stop(); wall = watch.ElapsedMilliseconds;
            }
            var total = page.GetProperty("totalCount").GetInt32(); var api = page.GetProperty("elapsedMs").GetInt64();
            Assert.True(wall < 2000, $"{query}: {wall} ms");
            if (query.StartsWith("search="))
            {
                var term = Uri.UnescapeDataString(query["search=".Length..]);
                var expected = (await env.ScriptAsync("database/a23_so_sanh_tim_kiem.sql",
                    text => Replace(text, "DECLARE @Search NVARCHAR(100) = N'React';", "DECLARE @Search NVARCHAR(100) = @SearchValue;"),
                    new SqlParameter("@SearchValue", term)))[0].Single()["SoCvKhop"];
                Assert.Equal(Convert.ToInt32(expected), total);
            }
            else Assert.All(page.GetProperty("items").EnumerateArray(), i => Assert.Contains(i.GetProperty("applications").EnumerateArray(),
                a => a.GetProperty("jobId").GetInt32() == jobId && a.GetProperty("stage").GetString() == ApplicationStages.Rejected));
            results.Add(new { query, wallMs = wall, apiElapsedMs = api, totalCount = total, rows = page.GetProperty("items").GetArrayLength() });
        }
        Assert.True((int)Convert.ToInt32(seeded["CoReact"]) > 0 && (int)Convert.ToInt32(seeded["CoSqlServer"]) > 0);
        await env.WriteAsync("performance", new { Cvs = seeded["SoCvGia"], TextMB = seeded["TongChuMB"], CoReact = seeded["CoReact"], CoSqlServer = seeded["CoSqlServer"], Results = results });

        var cleanup = (await env.ScriptAsync("database/a23_xoa_du_lieu_hieu_nang.sql"))[^1].Single();
        Assert.Equal((0, 0), (Convert.ToInt32(cleanup["CvConLai"]), Convert.ToInt32(cleanup["UngVienConLai"])));
        await using (var db = env.Context()) Assert.Equal(jobsBefore, await db.Jobs.CountAsync());
        Assert.Equal(0, (await GetAsync(client, "search=perf_")).GetProperty("totalCount").GetInt32());
    }

    private static string Replace(string text, string from, string to)
    {
        Assert.Contains(from, text);
        return text.Replace(from, to, StringComparison.Ordinal);
    }
}
