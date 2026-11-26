using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Backup;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Backup;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.E2E;

namespace SmartCv.Tests.Ops;

/// <summary>Clock the tests can move forward without waiting.</summary>
public sealed class ShiftingClock : TimeProvider
{
    public TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
}

public sealed class CapturingLogger : ILogger
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
    { lock (Lines) Lines.Add(format(state, error) + (error is null ? "" : error.ToString())); }
}

/// <summary>
/// A synthetic SCApp installation: FULL-recovery database migrated from the real migrations, CV files, key rings,
/// staging, an in-memory Drive and the real worker coordinators. Only SmartCv_A62_* / SmartCv_Restore_* catalogs.
/// </summary>
public sealed class A62Installation : IAsyncDisposable
{
    public string Catalog { get; } = "SmartCv_A62_Test_" + Guid.NewGuid().ToString("N");
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SmartCv_A62_" + Guid.NewGuid().ToString("N"));
    public SqlConnectionStringBuilder Server { get; } = new(Environment.GetEnvironmentVariable(A62SqlFact.Setting)) { InitialCatalog = "master", Pooling = false, TrustServerCertificate = true };
    public string Connection => new SqlConnectionStringBuilder(Server.ConnectionString) { InitialCatalog = Catalog, Pooling = true }.ConnectionString;
    public string CvBase => Path.Combine(Root, "source");
    public string Staging => Path.Combine(Root, "staging");
    public string Restores => Path.Combine(Root, "restore");
    public MemoryCloudStore Cloud { get; } = new();
    public ShiftingClock Clock { get; } = new();
    public CapturingLogger Log { get; } = new();
    public byte[] Key { get; set; } = RandomNumberGenerator.GetBytes(32);
    public byte[][] RetiredKeys { get; set; } = [];
    private B57World? world;
    internal B57World World => world!;

    internal static async Task<A62Installation> CreateAsync(Dictionary<string, string?>? settings = null, bool kestrel = false, Action<IServiceProviderHooks>? hooks = null)
    {
        var it = new A62Installation();
        foreach (var folder in new[] { "source/Uploads/CV", "api-keys", "web-keys", "restore", "staging" }) Directory.CreateDirectory(Path.Combine(it.Root, folder));
        foreach (var ring in new[] { "api-keys", "web-keys" })
            await File.WriteAllTextAsync(Path.Combine(it.Root, ring, "key-synthetic.xml"), "<key id=\"synthetic\"><descriptor/></key>");
        if (OperatingSystem.IsWindows())
        {
            // SQL Server service must write the staging .bak files of this synthetic fixture only.
            var start = new System.Diagnostics.ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { it.Root, "/grant", "*S-1-5-80-0:(OI)(CI)M", "/Q" }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        await it.Execute($"CREATE DATABASE [{it.Catalog}]; ALTER DATABASE [{it.Catalog}] SET RECOVERY FULL; ALTER DATABASE [{it.Catalog}] SET MULTI_USER;");
        await using (var db = it.Db()) await db.Database.MigrateAsync();
        var config = new Dictionary<string, string?> { ["CvUpload:BaseDirectory"] = it.CvBase };
        foreach (var pair in settings ?? []) config[pair.Key] = pair.Value;
        it.world = await B57World.CreateAsync(config, kestrel: kestrel, sqlConnection: it.Connection, services: hooks is null ? null : s => hooks(new(s, it)));
        foreach (var w in B57World.Workspaces)
            await it.World.WithDb(w, async db =>
            {
                foreach (var cv in await db.Cvs.ToListAsync())
                {
                    var bytes = TestSupport.Pdf("A62 SECRET CONTENT " + cv.CvId);
                    cv.StoredPath = $"Uploads/CV/w{w}/2026/11/{cv.CvId}.pdf";
                    var path = Path.Combine(it.CvBase, cv.StoredPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, bytes);
                    cv.FileHash = Convert.ToHexStringLower(SHA256.HashData(bytes)); cv.FileSizeBytes = bytes.Length;
                }
                await db.SaveChangesAsync();
            });
        // A normal full backup establishes the log chain, as the real database already has one.
        await it.Execute($"BACKUP DATABASE [{it.Catalog}] TO DISK = N'NUL';");
        await using (var db = it.Db())
        {
            db.BackupDriveConnections.Add(new BackupDriveConnection { BackupDriveConnectionId = 1, ProtectedCredential = "test", FolderId = "folder", FolderName = "SCApp Backups", ConnectedAtUtc = DateTime.UtcNow, AccountEmail = "owner@example.test", LastCheckedAtUtc = DateTime.UtcNow, LastCheckOk = true });
            await db.SaveChangesAsync();
        }
        return it;
    }

    public BackupWorkerOptions Options(string role) => new()
    {
        Role = role, CvBaseDirectory = CvBase, ApiKeysPath = Path.Combine(Root, "api-keys"), WebKeysPath = Path.Combine(Root, "web-keys"),
        StagingDirectory = Staging, KeyDirectory = Path.Combine(Root, "key"), RestoreFilesRoot = Restores, MinimumFreeSpaceMb = 0, PrivacyLockSeconds = 3
    };

    public BackupEngineContext Context(BackupWorkerOptions options) => new(options, Connection, Db, new StaticBackupKeyProvider(Key, RetiredKeys), Cloud, Clock, Log);
    public Task Tick(string role = BackupWorkerRoles.Backup, Action<BackupWorkerOptions>? change = null)
    {
        var options = Options(role); change?.Invoke(options);
        return new BackupCoordinator(Context(options)).TickAsync(default);
    }

    public SmartCvDbContext Db() => new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(Connection).Options);

    public async Task<Guid> Request(string type, Guid? point = null, DateTime? stopAt = null)
    {
        await using var db = Db();
        var (outcome, id, detail) = await new BackupAdminService(db, Clock).RequestAsync(type, 1, true, default, point, stopAt);
        Assert.True(outcome == BackupRequestOutcome.Accepted, detail);
        return id!.Value;
    }

    public async Task<BackupJob> Job(Guid id) { await using var db = Db(); return await db.BackupJobs.AsNoTracking().SingleAsync(j => j.BackupJobId == id); }
    public async Task<List<BackupJob>> Jobs() { await using var db = Db(); return await db.BackupJobs.AsNoTracking().OrderBy(j => j.RequestedAtUtc).ToListAsync(); }

    public async Task<object?> Execute(string sql, string? catalog = null)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(Server.ConnectionString) { InitialCatalog = catalog ?? "master" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Like an upload: the file is written first, then the row.</summary>
    public async Task<Guid> Upload(string text)
    {
        var id = Guid.NewGuid(); var bytes = TestSupport.Pdf(text);
        var workspace = (int)(await Execute("SELECT MIN(WorkspaceId) FROM dbo.Cvs", Catalog))!;
        var stored = $"Uploads/CV/w{workspace}/2026/11/{id}.pdf";
        var path = Path.Combine(CvBase, stored.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        await Execute("INSERT INTO dbo.Cvs (CvId,WorkspaceId,OriginalFileName,StoredPath,ContentType,FileSizeBytes,FileHash,ExtractionStatus,UploadedAt) " +
            $"VALUES ('{id}',{workspace},N'synthetic.pdf',N'{stored}',N'application/pdf',{bytes.Length},'{Convert.ToHexStringLower(SHA256.HashData(bytes))}',N'Completed',SYSUTCDATETIME())", Catalog);
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        world?.Dispose();
        SqlConnection.ClearAllPools();
        var databases = new List<string> { Catalog };
        if (Directory.Exists(Restores)) databases.AddRange(Directory.GetDirectories(Restores).Select(Path.GetFileName).Where(n => n is not null)!);
        foreach (var name in databases)
        {
            Assert.Matches("^(SmartCv_A62_Test|SmartCv_Restore)_[a-f0-9]{32}$", name);
            await Execute($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
        }
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

public sealed record IServiceProviderHooks(Microsoft.Extensions.DependencyInjection.IServiceCollection Services, A62Installation Installation);
