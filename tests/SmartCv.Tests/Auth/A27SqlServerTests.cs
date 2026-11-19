using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Api.Auth;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Auth;

/// <summary>Opt-in: chỉ tạo catalog GUID mới, xác nhận chưa tồn tại, giữ lại để review; không có DROP DATABASE.</summary>
public class A27SqlServerTests
{
    [A27SqlFact]
    public async Task Upgrade_and_rollback_preserve_legacy_history_and_trigger_still_blocks_update_delete()
    {
        var env = await SqlEnvironment.CreateAsync(A27MigrationTests.Previous);
        await using var db = env.Context();
        var cv = Guid.NewGuid();
        var hash = new string('a', 64);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Cvs(CvId,OriginalFileName,StoredPath,ContentType,FileSizeBytes,FileHash) VALUES({cv},N'A27-synthetic.pdf',N'synthetic',N'application/pdf',1,{hash})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Applications(CvId,JobId) VALUES({cv},1)");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO ApplicationEvents(ApplicationId,FromStage,ToStage,RejectReason,Note,Actor,CreatedAt)
            SELECT ApplicationId,N'New',N'Rejected',N'MissingSkills',N'Synthetic legacy note',N'Synthetic HR','2026-11-01T01:02:03.1234567' FROM Applications;
            """);
        var fingerprint = await LegacyFingerprint(db);
        await db.GetService<IMigrator>().MigrateAsync(A27MigrationTests.Current);
        Assert.Equal(fingerprint, await LegacyFingerprint(db));
        Assert.Null((await db.ApplicationEvents.AsNoTracking().SingleAsync()).UserId);

        var appId = await db.Applications.Select(a => a.ApplicationId).SingleAsync();
        db.ApplicationEvents.Add(new() { ApplicationId = appId, FromStage = "New", ToStage = "Reviewing", Actor = "Synthetic after A27", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(); // EF phải tiếp tục dùng insert tương thích trigger, không OUTPUT trực tiếp.
        Assert.Equal(2, await db.ApplicationEvents.CountAsync());
        foreach (var sql in new[] { "UPDATE ApplicationEvents SET Note=N'changed'", "DELETE FROM ApplicationEvents" })
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(51000, error.Number);
        }
        var afterInsert = await LegacyFingerprint(db);
        await db.GetService<IMigrator>().MigrateAsync(A27MigrationTests.Previous);
        Assert.Equal(afterInsert, await LegacyFingerprint(db));
        await db.GetService<IMigrator>().MigrateAsync(A27MigrationTests.Current);
        Assert.Equal(afterInsert, await LegacyFingerprint(db));
        Assert.All(await db.ApplicationEvents.AsNoTracking().ToListAsync(), e => Assert.Null(e.UserId));
    }

    [A27SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_seeders_on_independent_connections_create_one_admin_even_with_different_emails(bool sameEmail)
    {
        var env = await SqlEnvironment.CreateAsync();
        await using var first = env.Context();
        var observer = new LockAttemptObserver();
        await using var second = env.Context(observer);
        var seed1 = SeedAdminPlanTests.Valid("one@example.invalid");
        var seed2 = SeedAdminPlanTests.Valid(sameEmail ? seed1.Email! : "two@example.invalid");
        using var hasher = new BlockingHasher();
        var task1 = Task.Run(() => AdminSeeder.SeedAdminAsync(first, hasher, seed1, NullLogger.Instance));
        Task? task2 = null;
        try
        {
            await hasher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            task2 = AdminSeeder.SeedAdminAsync(second, new PasswordHasher<User>(), seed2, NullLogger.Instance);
            await observer.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(task2.IsCompleted);
        }
        finally { hasher.Release.Set(); }
        await task1;
        await task2!;
        await using var check = env.Context();
        var saved = await check.Users.SingleAsync();
        Assert.Equal(seed1.Email, saved.Email);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<User>().VerifyHashedPassword(saved, saved.PasswordHash, seed1.Password!));
        var original = (saved.PasswordHash, saved.SecurityStamp, saved.CreatedAt);
        await AdminSeeder.SeedAdminAsync(check, new PasswordHasher<User>(), seed2, NullLogger.Instance);
        check.ChangeTracker.Clear();
        saved = await check.Users.SingleAsync();
        Assert.Equal(original, (saved.PasswordHash, saved.SecurityStamp, saved.CreatedAt));
    }

    [A27SqlFact]
    public async Task Failed_seeding_rolls_back_and_releases_transaction_lock_for_retry()
    {
        var env = await SqlEnvironment.CreateAsync();
        var seed = SeedAdminPlanTests.Valid();
        await using (var failing = env.Context())
            await Assert.ThrowsAsync<InvalidOperationException>(() => AdminSeeder.SeedAdminAsync(failing, new ThrowingHasher(), seed, NullLogger.Instance));
        await using var retry = env.Context();
        Assert.Empty(await retry.Users.ToListAsync());
        await AdminSeeder.SeedAdminAsync(retry, new PasswordHasher<User>(), seed, NullLogger.Instance);
        Assert.Single(await retry.Users.ToListAsync());
    }

    [A27SqlFact]
    public async Task SQL_enforces_exact_roles_unique_email_and_non_cascading_history_FK()
    {
        var env = await SqlEnvironment.CreateAsync();
        await using var db = env.Context();
        var seed = SeedAdminPlanTests.Valid();
        await AdminSeeder.SeedAdminAsync(db, new PasswordHasher<User>(), seed, NullLogger.Instance);
        var user = await db.Users.SingleAsync();
        foreach (var role in new[] { "Manager", "admin", "HR ", "Admin " })
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Users(Email,FullName,PasswordHash,Role,IsActive,MustChangePassword,SecurityStamp) VALUES(N'other@example.invalid',N'Synthetic',N'synthetic-hash',{role},1,0,N'synthetic-stamp')"));
            Assert.Equal(547, error.Number);
        }
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Users(Email,FullName,PasswordHash,Role,IsActive,MustChangePassword,SecurityStamp) VALUES({user.Email},N'Synthetic',N'synthetic-hash',N'HR',1,0,N'synthetic-stamp')"));
        Assert.Contains(duplicate.Number, new[] { 2601, 2627 });
        var app = Applications.ApplicationStageEndpointTests.NewApplication();
        db.Add(app); await db.SaveChangesAsync();
        var invalidFk = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationEvents(ApplicationId,FromStage,ToStage,Actor,UserId) VALUES({app.ApplicationId},N'New',N'Reviewing',N'Synthetic',{int.MaxValue})"));
        Assert.Equal(547, invalidFk.Number);
        db.Add(new ApplicationEvent { ApplicationId = app.ApplicationId, FromStage = "New", ToStage = "Reviewing", Actor = "Synthetic", UserId = user.UserId });
        await db.SaveChangesAsync();
        var delete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Users WHERE UserId={user.UserId}"));
        Assert.Equal(547, delete.Number);
        Assert.Single(await db.ApplicationEvents.ToListAsync());
    }

    private static async Task<string> LegacyFingerprint(SmartCvDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            var contents = new StringBuilder();
            foreach (var sql in new[]
            {
                "SELECT EventId,ApplicationId,FromStage,ToStage,RejectReason,Note,Actor,CreatedAt FROM ApplicationEvents ORDER BY EventId FOR JSON PATH,INCLUDE_NULL_VALUES",
                "SELECT * FROM Applications ORDER BY ApplicationId FOR JSON PATH,INCLUDE_NULL_VALUES",
                "SELECT * FROM Cvs ORDER BY CvId FOR JSON PATH,INCLUDE_NULL_VALUES",
                "SELECT * FROM EvaluationRuns ORDER BY RunId FOR JSON PATH,INCLUDE_NULL_VALUES",
                "SELECT * FROM EvaluationSets ORDER BY EvaluationSetId FOR JSON PATH,INCLUDE_NULL_VALUES",
                "SELECT * FROM Jobs ORDER BY JobId FOR JSON PATH,INCLUDE_NULL_VALUES"
            })
            {
                await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = sql;
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) contents.Append(reader.GetString(0));
                contents.Append('|');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents.ToString())));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private sealed class SqlEnvironment(string connection)
    {
        public SmartCvDbContext Context(IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new(options.Options);
        }

        public static async Task<SqlEnvironment> CreateAsync(string target = A27MigrationTests.Current)
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A27SqlFactAttribute.Setting));
            A27SqlFactAttribute.Validate(builder);
            builder.InitialCatalog = A27SqlFactAttribute.Prefix + Guid.NewGuid().ToString("N");
            builder.Pooling = false; builder.ConnectTimeout = 10;
            Assert.Matches("^SmartCv_A27_Test_[0-9a-f]{32}$", builder.InitialCatalog);
            var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
            await using (var sql = new SqlConnection(master.ConnectionString))
            {
                await sql.OpenAsync();
                await using var check = new SqlCommand("SELECT DB_ID(@catalog)", sql);
                check.Parameters.AddWithValue("@catalog", builder.InitialCatalog);
                Assert.Equal(DBNull.Value, await check.ExecuteScalarAsync());
                await using var create = new SqlCommand($"CREATE DATABASE [{builder.InitialCatalog}]; ALTER DATABASE [{builder.InitialCatalog}] SET MULTI_USER;", sql);
                await create.ExecuteNonQueryAsync();
            }
            var root = TestSupport.RepoFile("artifacts/a27/sql-catalogs"); Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, builder.InitialCatalog + ".txt"), "Retained catalog: " + builder.InitialCatalog);
            var env = new SqlEnvironment(builder.ConnectionString);
            await using var db = env.Context();
            await db.Database.OpenConnectionAsync();
            await using (var check = db.Database.GetDbConnection().CreateCommand())
            {
                check.CommandText = "SELECT DB_NAME()";
                Assert.Equal(builder.InitialCatalog, await check.ExecuteScalarAsync());
            }
            await db.GetService<IMigrator>().MigrateAsync(target);
            return env;
        }
    }

    private sealed class LockAttemptObserver : DbCommandInterceptor
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal)) Attempted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingHasher : IPasswordHasher<User>, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public string HashPassword(User user, string password)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(25))) throw new TimeoutException("Test hash barrier timed out.");
            return new PasswordHasher<User>().HashPassword(user, password);
        }
        public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword) => throw new NotSupportedException();
        public void Dispose() => Release.Dispose();
    }

    private sealed class ThrowingHasher : IPasswordHasher<User>
    {
        public string HashPassword(User user, string password) => throw new InvalidOperationException("Synthetic hash failure.");
        public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword) => throw new NotSupportedException();
    }
}

public sealed class A27SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A27_TEST_SQL_CONNECTION";
    public const string Prefix = "SmartCv_A27_Test_";
    public A27SqlFactAttribute() => Skip = GetSkip();
    internal static string? GetSkip()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value)) return $"Set {Setting} for new retained A27 test catalogs.";
        try { Validate(new SqlConnectionStringBuilder(value)); return null; }
        catch (ArgumentException) { return "A27 SQL requires a designated test catalog and no AttachDBFilename."; }
    }
    internal static void Validate(SqlConnectionStringBuilder value)
    {
        if (string.IsNullOrWhiteSpace(value.DataSource) || !value.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(value.AttachDBFilename))
            throw new ArgumentException("Only designated A27 SQL test connections are allowed.");
    }
}

public sealed class A27SqlTheoryAttribute : TheoryAttribute
{
    public A27SqlTheoryAttribute() => Skip = A27SqlFactAttribute.GetSkip();
}
