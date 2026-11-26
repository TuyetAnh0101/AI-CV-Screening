using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SmartCv.Core.Backup;

namespace SmartCv.Infrastructure.Backup;

public sealed record CvRow(Guid CvId, string StoredPath, string Sha256, long Bytes);
public sealed record SqlBackupLsn(decimal First, decimal Last, DateTime FinishedServerTime);
public sealed record SqlDatabaseInfo(string RecoveryModel, string Edition, int EngineEdition, string Version)
{
    // Standard/Enterprise/Developer support compressed backups; Express does not.
    public string CompressionClause => EngineEdition is 2 or 3 ? ", COMPRESSION" : "";
}

/// <summary>
/// One SQL session for one backup step. Uses only: BACKUP DATABASE/LOG (db_backupoperator), SELECT on five
/// Cvs columns, VIEW DEFINITION, msdb backup history and __EFMigrationsHistory. Never reads business tables.
/// </summary>
public sealed class BackupSql : IAsyncDisposable
{
    /// <summary>SCApp (A39) takes this EXCLUSIVE for every CV erasure. Shared here: uploads and reads continue.</summary>
    public const string PrivacyResource = "SmartCv:A39:privacy";
    private readonly SqlConnection connection;
    private readonly int timeout;
    public string Database { get; }

    private BackupSql(SqlConnection connection, int timeout) { this.connection = connection; this.timeout = timeout; Database = connection.Database; }

    public static async Task<BackupSql> OpenAsync(string connectionString, int commandTimeoutSeconds, CancellationToken ct, string? catalog = null)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false };
        if (catalog is not null) builder.InitialCatalog = catalog;
        var connection = new SqlConnection(builder.ConnectionString);
        try { await connection.OpenAsync(ct); return new BackupSql(connection, commandTimeoutSeconds); }
        catch (SqlException) { await connection.DisposeAsync(); throw new BackupException(BackupErrors.SqlConnect); }
    }

    public SqlCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql; command.CommandTimeout = timeout;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public async Task<object?> ScalarAsync(string sql, CancellationToken ct, params (string, object?)[] parameters)
    {
        await using var command = Command(sql, parameters);
        return await command.ExecuteScalarAsync(ct);
    }

    public async Task ExecuteAsync(string sql, string failureCode, CancellationToken ct, params (string, object?)[] parameters)
    {
        await using var command = Command(sql, parameters);
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (SqlException) when (!ct.IsCancellationRequested) { throw new BackupException(failureCode); }
    }

    /// <summary>Session app lock; returns false immediately when someone else holds it.</summary>
    public async Task<bool> TryLockAsync(string resource, string mode, int timeoutMs, CancellationToken ct)
    {
        var result = await ScalarAsync("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode=@mode,@LockOwner='Session',@LockTimeout=@timeout; SELECT @r;",
            ct, ("@resource", resource), ("@mode", mode), ("@timeout", timeoutMs));
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) >= 0;
    }

    public async Task<string?> LockModeAsync(string resource, CancellationToken ct) =>
        await ScalarAsync("SELECT APPLOCK_MODE(N'public',@resource,N'Session');", ct, ("@resource", resource)) as string;

    public Task ReleaseAsync(string resource, CancellationToken ct) =>
        ExecuteAsync("EXEC sys.sp_releaseapplock @Resource=@resource,@LockOwner='Session';", BackupErrors.Consistency, ct, ("@resource", resource));

    public async Task EnterPrivacyAsync(int seconds, CancellationToken ct)
    {
        if (!await TryLockAsync(PrivacyResource, "Shared", seconds * 1000, ct)) throw new BackupException(BackupErrors.Locked);
    }

    public async Task AssertPrivacyHeldAsync(CancellationToken ct)
    {
        if (await LockModeAsync(PrivacyResource, ct) != "Shared") throw new BackupException(BackupErrors.Consistency);
    }

    public async Task<SqlDatabaseInfo> DatabaseInfoAsync(CancellationToken ct)
    {
        await using var command = Command("SELECT recovery_model_desc,state_desc,CONVERT(nvarchar(128),SERVERPROPERTY('Edition')),CONVERT(int,SERVERPROPERTY('EngineEdition')),CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')) FROM sys.databases WHERE name=DB_NAME();");
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(1) != "ONLINE") throw new BackupException(BackupErrors.Database);
        return new(reader.GetString(0), reader.GetString(2), reader.GetInt32(3), reader.GetString(4));
    }

    public async Task<long> AllocatedBytesAsync(CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync("SELECT SUM(CONVERT(bigint,size))*8192 FROM sys.database_files;", ct), CultureInfo.InvariantCulture);

    public async Task<long> LogUsedBytesAsync(CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync("SELECT COALESCE(SUM(CONVERT(bigint,FILEPROPERTY(name,'SpaceUsed'))),0)*8192 FROM sys.database_files WHERE type=1;", ct), CultureInfo.InvariantCulture);

    /// <summary>Live CV references only; erased rows (A39 "(da-xoa)") have no file by design.</summary>
    public async Task<List<CvRow>> CvRowsAsync(CancellationToken ct)
    {
        var rows = new List<CvRow>();
        await using var command = Command("SELECT CvId,StoredPath,FileHash,FileSizeBytes,AnonymizedAt FROM dbo.Cvs;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var stored = reader.GetString(1);
            if (stored == "(da-xoa)" && !reader.IsDBNull(4)) continue;
            rows.Add(new(reader.GetGuid(0), stored, reader.GetString(2).ToLowerInvariant(), reader.GetInt64(3)));
        }
        return rows;
    }

    /// <summary>LSN range SQL Server recorded for the file just written; null when msdb history is not readable.</summary>
    public async Task<SqlBackupLsn?> BackupLsnAsync(string device, CancellationToken ct)
    {
        try
        {
            await using var command = Command("SELECT TOP 1 bs.first_lsn,bs.last_lsn,bs.backup_finish_date FROM msdb.dbo.backupset bs JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id=bs.media_set_id WHERE mf.physical_device_name=@device ORDER BY bs.backup_set_id DESC;", ("@device", device));
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? new(reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDateTime(2)) : null;
        }
        catch (SqlException) { return null; }
    }

    public async Task<string?> SchemaVersionAsync(CancellationToken ct)
    {
        try { return await ScalarAsync("SELECT TOP 1 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;", ct) as string; }
        catch (SqlException) { return null; }
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();
}
