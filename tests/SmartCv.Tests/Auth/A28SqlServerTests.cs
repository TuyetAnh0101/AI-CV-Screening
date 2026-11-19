using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using System.Net;
using System.Net.Http.Json;

namespace SmartCv.Tests.Auth;

public class A28SqlServerTests
{
    [A28ReadOnlyAppDbFact]
    public async Task Inspect_configured_Development_database_without_seeding_or_schema_or_data_writes()
    {
        // A29 fail-safe: the only scope allowed to reach SmartCvDb; every statement below is a SELECT.
        using var readOnlyInspection = ApplicationDatabaseGuard.AllowReadOnlyApplicationInspection();
        var config = new ConfigurationBuilder().SetBasePath(TestSupport.RepoFile("src/SmartCv.Api"))
            .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets(typeof(Program).Assembly, optional: true).AddEnvironmentVariables().Build();
        using var configLifetime = (IDisposable)config;
        var output = new Dictionary<string, object?> { ["ReadOnly"] = true, ["Environment"] = "Development, no command-line override" };
        try
        {
            var connection = new SqlConnectionStringBuilder(config.GetConnectionString("SmartCvDb")) { ConnectTimeout = 5 };
            output["DataSource"] = connection.DataSource; output["Catalog"] = connection.InitialCatalog;
            output["JwtConfigurationError"] = config.GetSection("Jwt").Get<JwtOptions>()?.Validate();
            await using var sql = new SqlConnection(connection.ConnectionString); await sql.OpenAsync();
            async Task<List<Dictionary<string, object?>>> Read(string query)
            {
                await using var command = new SqlCommand(query, sql) { CommandTimeout = 5 };
                await using var reader = await command.ExecuteReaderAsync();
                var rows = new List<Dictionary<string, object?>>();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object?>();
                    for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                return rows;
            }
            output["ActualTarget"] = await Read("SELECT CONVERT(nvarchar(128),@@SERVERNAME) AS ServerName,DB_NAME() AS CatalogName");
            var migrations = await Read("SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId");
            output["MigrationHistory"] = migrations;
            output["AddUsersApplied"] = migrations.Any(r => (string?)r["MigrationId"] == "20261119110208_AddUsers");
            var columns = await Read("SELECT c.name AS ColumnName,t.name AS SqlType,c.max_length AS MaxLengthBytes,c.is_nullable,c.is_identity FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(N'dbo.Users') ORDER BY c.column_id");
            output["UserColumns"] = columns;
            output["EventUserColumn"] = await Read("SELECT name,is_nullable FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ApplicationEvents') AND name=N'UserId'");
            if (columns.Count > 0) output["AdminCounts"] = await Read("SELECT COUNT(*) AS ActiveAdmins FROM dbo.Users WHERE Role=N'Admin' AND IsActive=1");
            output["InspectionSucceeded"] = true;
        }
        catch (Exception error)
        {
            output["InspectionSucceeded"] = false; output["ErrorType"] = error.GetType().Name;
            if (error is SqlException sql) output["SqlErrorNumbers"] = sql.Errors.Cast<SqlError>().Select(e => e.Number).ToArray();
        }
        await File.WriteAllTextAsync(TestSupport.RepoFile("artifacts/a28/database-readiness-dotnet.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(output.TryGetValue("InspectionSucceeded", out var succeeded) && succeeded is true, "Read-only inspection could not complete; see sanitized artifact.");
        Assert.True(output["AddUsersApplied"] is true, "AddUsers is pending. No migration was applied by this test.");
    }

    [A28SqlFact]
    public async Task SQL_login_and_me_persist_last_login_and_preserve_credentials_and_history()
    {
        var env = await SqlEnvironment.Create();
        using var factory = env.Factory(); using var http = factory.CreateClient();
        foreach (var role in new[] { "Admin", "HR" })
        {
            var password = A28TestSupport.Password(); var user = await A28TestSupport.Seed(factory, password, role);
            using var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = user.Email.ToUpperInvariant(), Password = password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var result = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
            http.DefaultRequestHeaders.Authorization = new("Bearer", result.Token);
            using var me = await http.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            var dto = (await me.Content.ReadFromJsonAsync<MeDto>())!;
            Assert.Equal(role, dto.Role); Assert.Equal(DateTimeKind.Utc, dto.LastLoginAt!.Value.Kind);
            await using var check = env.Db(); var saved = await check.Users.SingleAsync(u => u.UserId == user.UserId);
            Assert.Equal(user.PasswordHash, saved.PasswordHash); Assert.Equal(user.SecurityStamp, saved.SecurityStamp); Assert.NotNull(saved.LastLoginAt);
            Assert.Empty(await check.ApplicationEvents.ToListAsync()); Assert.False(check.Database.HasPendingModelChanges());
        }
    }

    [A28SqlFact]
    public async Task SQL_login_cannot_overwrite_password_and_stamp_rotated_while_the_old_password_is_verified()
    {
        var env = await SqlEnvironment.Create();
        Func<Task>? rotate = null;
        var hasher = new RotationHasher(() => rotate?.Invoke().GetAwaiter().GetResult());
        using var factory = env.Factory().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        { s.RemoveAll<IPasswordHasher<User>>(); s.AddScoped<IPasswordHasher<User>>(_ => hasher); }));
        using var http = factory.CreateClient();
        var oldPassword = A28TestSupport.Password(); var nextPassword = A28TestSupport.Password();
        var user = await A28TestSupport.Seed(factory, oldPassword, oldHash: true);
        var nextHash = new PasswordHasher<User>().HashPassword(user, nextPassword); var nextStamp = User.NewStamp();
        rotate = async () =>
        {
            rotate = null;
            await using var db = env.Db();
            await db.Users.Where(u => u.UserId == user.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, nextHash).SetProperty(u => u.SecurityStamp, nextStamp));
        };
        using var stale = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = user.Email, Password = oldPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        await using var verify = env.Db(); var saved = await verify.Users.SingleAsync();
        Assert.Equal(nextHash, saved.PasswordHash); Assert.Equal(nextStamp, saved.SecurityStamp); Assert.Null(saved.LastLoginAt);
        using var current = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = user.Email, Password = nextPassword });
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    private sealed class RotationHasher(Action afterVerify) : IPasswordHasher<User>
    {
        public string HashPassword(User user, string password) => new PasswordHasher<User>().HashPassword(user, password);
        public PasswordVerificationResult VerifyHashedPassword(User user, string hash, string password)
        {
            var result = new PasswordHasher<User>().VerifyHashedPassword(user, hash, password);
            afterVerify(); return result;
        }
    }

    private sealed class SqlEnvironment(string connection)
    {
        internal SmartCvDbContext Db() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);
        internal WebApplicationFactory<Program> Factory() => A28TestSupport.Factory().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<DbContextOptions<SmartCvDbContext>>(); s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            s.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(connection));
        }));
        internal static async Task<SqlEnvironment> Create()
        {
            var target = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A28SqlFact.Setting));
            A28SqlFact.Validate(target);
            target.InitialCatalog = "SmartCv_A28_Test_" + Guid.NewGuid().ToString("N"); target.Pooling = false; target.ConnectTimeout = 10;
            Assert.Matches("^SmartCv_A28_Test_[0-9a-f]{32}$", target.InitialCatalog);
            var master = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" };
            await using (var sql = new SqlConnection(master.ConnectionString))
            {
                await sql.OpenAsync();
                await using var check = new SqlCommand("SELECT DB_ID(@catalog)", sql); check.Parameters.AddWithValue("@catalog", target.InitialCatalog);
                Assert.Equal(DBNull.Value, await check.ExecuteScalarAsync());
                await using var create = new SqlCommand($"CREATE DATABASE [{target.InitialCatalog}]; ALTER DATABASE [{target.InitialCatalog}] SET MULTI_USER;", sql);
                await create.ExecuteNonQueryAsync();
            }
            var env = new SqlEnvironment(target.ConnectionString);
            await File.AppendAllTextAsync(TestSupport.RepoFile("artifacts/a28/sql-catalogs.txt"), target.InitialCatalog + Environment.NewLine);
            await using var db = env.Db(); await db.Database.OpenConnectionAsync();
            await using (var check = db.Database.GetDbConnection().CreateCommand())
            { check.CommandText = "SELECT DB_NAME()"; Assert.Equal(target.InitialCatalog, await check.ExecuteScalarAsync()); }
            await db.Database.MigrateAsync(); // Chỉ catalog GUID mới đã xác nhận; không cleanup/drop catalog.
            return env;
        }
    }
}

public sealed class A28ReadOnlyAppDbFact : FactAttribute
{
    public A28ReadOnlyAppDbFact()
    { if (Environment.GetEnvironmentVariable("SMARTCV_A28_INSPECT_APP_DB") != "1") Skip = "NOT RUN: explicit read-only application database inspection is opt-in."; }
}

public sealed class A28SqlFact : FactAttribute
{
    public const string Setting = "SMARTCV_A28_TEST_SQL_CONNECTION";
    public A28SqlFact()
    {
        var connection = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(connection)) { Skip = "NOT RUN: set A28 SQL test connection for new retained GUID catalogs."; return; }
        try { Validate(new(connection)); } catch (ArgumentException) { Skip = "NOT RUN: A28 SQL requires a designated test catalog without attached files."; }
    }
    internal static void Validate(SqlConnectionStringBuilder target)
    {
        if (string.IsNullOrWhiteSpace(target.DataSource) || !target.InitialCatalog.StartsWith("SmartCv_A28_Test_", StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(target.AttachDBFilename))
            throw new ArgumentException("Only explicitly designated A28 SQL test connections are accepted.");
    }
}
