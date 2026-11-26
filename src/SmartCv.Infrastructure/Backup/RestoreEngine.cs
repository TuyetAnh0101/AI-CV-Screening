using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Backup;
using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Backup;

public sealed record RestoreEvidence(string TargetDatabase, Guid PointId, int LogsApplied, DateTime? StopAtServerTime, string? SchemaVersion,
    bool CheckDbPassed, int CvFilesRestored, int CvFilesAcknowledgedMissing, IReadOnlyDictionary<string, long> TableRows, bool KeyRingsRestored);

/// <summary>
/// Restore-role pipeline: Drive → verify → decrypt → RESTORE into a NEW database → CHECKDB → rebuild and hash-check
/// every CV the restored rows reference → key rings. Never touches the source database or an existing database.
/// </summary>
public sealed class RestoreEngine(BackupEngineContext c)
{
    public static readonly string[] EvidenceTables = ["Users", "Workspaces", "WorkspaceMembers", "Jobs", "Cvs", "Applications", "Evaluations", "AuditEvents"];

    public async Task<RestoreEvidence> RunAsync(BackupJob job, ProgressReporter progress, CancellationToken ct)
    {
        BackupPoint full; List<BackupPoint> logs;
        IBackupCloudStore store;
        await using (var db = c.Db())
        {
            store = await c.Cloud.OpenAsync(db, ct);
            full = job.BackupPointId is { } chosen
                ? await db.BackupPoints.SingleOrDefaultAsync(p => p.BackupPointId == chosen, ct) ?? throw new BackupException(BackupErrors.DriveMissing)
                : await db.BackupPoints.Where(p => p.Kind == BackupPointKinds.Full && p.Status == BackupPointStatuses.Available && p.DatabaseName == c.Database)
                    .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct) ?? throw new BackupException(BackupErrors.DriveMissing);
            if (full.Kind != BackupPointKinds.Full || full.Status != BackupPointStatuses.Available) throw new BackupException(BackupErrors.DriveMissing);
            logs = full.LastLsn is null ? [] : await db.BackupPoints.Where(p => p.Kind == BackupPointKinds.Log && p.Status == BackupPointStatuses.Available
                && p.DatabaseName == full.DatabaseName && p.LastLsn > full.LastLsn).OrderBy(p => p.FirstLsn).ToListAsync(ct);
        }
        job.BackupPointId = full.BackupPointId;
        var chain = SelectChain(full, logs, job.StopAtServerTime);

        var target = "SmartCv_Restore_" + Guid.NewGuid().ToString("N");
        if (!BackupLimits.RestoreDatabase.IsMatch(target)) throw new BackupException(BackupErrors.Config);
        job.TargetDatabase = target;
        var work = Path.Combine(c.Options.RestoreFilesRoot, target);
        Directory.CreateDirectory(work);
        var created = false;
        try
        {
            await progress("download", 5);
            var manifest = await BackupManifest.DownloadAsync(store, c.Keys, full.ManifestFileId, ct);
            if (manifest.PointId != full.BackupPointId || manifest.Payload.CipherSha256 != full.PayloadCipherSha256) throw new BackupException(BackupErrors.Checksum);
            var logManifests = new List<BackupManifest>();
            foreach (var log in chain) logManifests.Add(await BackupManifest.DownloadAsync(store, c.Keys, log.ManifestFileId, ct));
            // CV lists of every later log (also beyond STOPAT) can supply files an earlier log failed to record.
            var lookup = logManifests.ToList();
            foreach (var log in logs.Where(l => chain.All(c2 => c2.BackupPointId != l.BackupPointId)))
                lookup.Add(await BackupManifest.DownloadAsync(store, c.Keys, log.ManifestFileId, ct));
            c.EnsureSpace(work, manifest.Payload.PlainBytes * 3 + manifest.CvFiles.Sum(f => f.Bytes) + logManifests.Sum(m => m.Payload.PlainBytes * 2));

            await progress("decrypt", 15);
            var bak = await DownloadPlainAsync(store, manifest.KeyId, manifest.Payload, Path.Combine(work, "database.bak"), ct);
            var trns = new List<string>();
            foreach (var (log, index) in logManifests.Select((m, i) => (m, i)))
                trns.Add(await DownloadPlainAsync(store, log.KeyId, log.Payload, Path.Combine(work, $"log-{index:D4}.trn"), ct));

            await progress("restore", 35);
            await using var sql = await BackupSql.OpenAsync(c.ConnectionString, c.Options.CommandTimeoutSeconds, ct, catalog: "master");
            if (await sql.ScalarAsync("SELECT DB_ID(@name);", ct, ("@name", target)) is not DBNull) throw new BackupException(BackupErrors.Restore);
            await sql.ExecuteAsync("RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;", BackupErrors.Restore, ct, ("@path", bak));
            var moves = new List<string>(); var parameters = new List<(string, object?)> { ("@db", target), ("@path", bak) };
            await using (var list = sql.Command("RESTORE FILELISTONLY FROM DISK=@path;", ("@path", bak)))
            await using (var reader = await list.ExecuteReaderAsync(ct))
            {
                var n = 0;
                while (await reader.ReadAsync(ct))
                {
                    var type = reader.GetString(reader.GetOrdinal("Type"));
                    if (type is not ("D" or "L")) throw new BackupException(BackupErrors.Restore);
                    var extension = type == "L" ? ".ldf" : n == 0 ? ".mdf" : ".ndf";
                    parameters.Add(($"@l{n}", reader.GetString(reader.GetOrdinal("LogicalName"))));
                    parameters.Add(($"@p{n}", Path.Combine(work, $"data-{n}{extension}")));
                    moves.Add($"MOVE @l{n} TO @p{n}"); n++;
                }
            }
            created = true;
            var recovery = trns.Count > 0 ? "NORECOVERY" : "RECOVERY";
            await sql.ExecuteAsync($"RESTORE DATABASE @db FROM DISK=@path WITH CHECKSUM, {recovery}, {string.Join(", ", moves)};", BackupErrors.Restore, ct, [.. parameters]);
            foreach (var trn in trns)
            {
                var stop = job.StopAtServerTime is { } at ? ", STOPAT=@stop" : "";
                await sql.ExecuteAsync($"RESTORE LOG @db FROM DISK=@path WITH CHECKSUM, NORECOVERY{stop};", BackupErrors.Restore, ct,
                    ("@db", target), ("@path", trn), ("@stop", job.StopAtServerTime));
            }
            if (trns.Count > 0) await sql.ExecuteAsync("RESTORE DATABASE @db WITH RECOVERY;", BackupErrors.Restore, ct, ("@db", target));
            foreach (var plain in trns.Append(bak)) File.Delete(plain);

            await progress("checkdb", 55);
            await ClaimRestoredAsync(sql, target, ct);
            // A new session: the one that restored keeps the permissions it had before it became the owner.
            await using var owner = await BackupSql.OpenAsync(c.ConnectionString, c.Options.CommandTimeoutSeconds, ct, catalog: "master");
            await using (var check = owner.Command("DBCC CHECKDB (@db) WITH NO_INFOMSGS, ALL_ERRORMSGS;", ("@db", target)))
            {
                try { await check.ExecuteNonQueryAsync(ct); }
                catch (SqlException error) when (!ct.IsCancellationRequested)
                {
                    // The number only: messages can name objects and pages of the restored data.
                    c.Logger.LogError("DBCC CHECKDB of the restored database failed with SQL error {Number}", error.Number);
                    throw new BackupException(BackupErrors.Compare);
                }
            }
            await using var restored = await BackupSql.OpenAsync(c.ConnectionString, c.Options.CommandTimeoutSeconds, ct, catalog: target);
            var schema = await restored.SchemaVersionAsync(ct);
            var expected = logManifests.Count > 0 ? logManifests[^1].AppSchemaVersion : manifest.AppSchemaVersion;
            if (expected is not null && schema != expected && job.StopAtServerTime is null)
            {
                c.Logger.LogError("Restored schema version {Found} differs from the backup's {Expected}", schema ?? "(unreadable)", expected);
                throw new BackupException(BackupErrors.Compare);
            }

            await progress("rebuild-cv", 70);
            var (restoredFiles, acknowledged) = await RebuildCvsAsync(store, restored, manifest, lookup, Path.Combine(work, "files"), ct);

            await progress("keys", 90);
            var keys = false;
            if (manifest.Keys is { } keyFile)
            {
                var archive = await DownloadPlainAsync(store, manifest.KeyId, keyFile, Path.Combine(work, "keys.zip"), ct);
                using (var zip = ZipFile.OpenRead(archive))
                {
                    if (!zip.Entries.Any(e => e.FullName.StartsWith("api/key-", StringComparison.Ordinal)) || !zip.Entries.Any(e => e.FullName.StartsWith("web/key-", StringComparison.Ordinal)))
                        throw new BackupException(BackupErrors.Keys);
                    zip.ExtractToDirectory(Path.Combine(work, "keys"));
                }
                File.Delete(archive); keys = true;
            }

            var rows = new Dictionary<string, long>();
            foreach (var table in EvidenceTables)
                rows[table] = Convert.ToInt64(await restored.ScalarAsync($"SELECT COUNT_BIG(*) FROM dbo.[{table}];", ct), CultureInfo.InvariantCulture);
            var evidence = new RestoreEvidence(target, full.BackupPointId, trns.Count, job.StopAtServerTime, schema, true, restoredFiles, acknowledged, rows, keys);
            await File.WriteAllTextAsync(Path.Combine(work, "restore-result.json"), JsonSerializer.Serialize(evidence, BackupManifest.Json), ct);
            await progress("done", 98);

            if (job.Type == BackupJobTypes.RestoreDrill && !c.Options.KeepDrillDatabase)
            {
                await restored.DisposeAsync();
                SqlConnection.ClearAllPools();
                await DropOwnAsync(owner, target, ct);
                BackupEngine.DeleteDirectory(work);
                job.TargetDatabase = null;
            }
            return evidence;
        }
        finally
        {
            // Plaintext backup files never outlive the job, whatever happened. A failed restore keeps its database for review.
            DeleteTransientFiles(work);
            if (!created) BackupEngine.DeleteDirectory(work);
        }
    }

    /// <summary>Database backups, logs, downloads and the key ring archive; never database files or restored CVs.</summary>
    public static void DeleteTransientFiles(string work)
    {
        if (!Directory.Exists(work)) return;
        string[] transient = [".bak", ".trn", ".enc", ".zip"];
        foreach (var file in Directory.EnumerateFiles(work).Where(f => transient.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            try { File.Delete(file); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// RESTORE keeps the source database's owner inside the file, so a least-privilege account is not owner of what
    /// it just restored and cannot check or drop it. The signed procedure fixes that for exactly this database (see
    /// <see cref="BackupGrantScript"/>). An administrator (development, tests) needs nothing.
    /// </summary>
    private async Task ClaimRestoredAsync(BackupSql sql, string target, CancellationToken ct)
    {
        if (!BackupLimits.RestoreDatabase.IsMatch(target)) throw new BackupException(BackupErrors.Config);
        if (await sql.ScalarAsync("SELECT IS_SRVROLEMEMBER('sysadmin');", ct) is 1) return;
        try
        {
            await using var claim = sql.Command($"EXEC master.dbo.{BackupGrantScript.ClaimProcedure} @name=@db;", ("@db", target));
            await claim.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException error) when (!ct.IsCancellationRequested)
        {
            c.Logger.LogError("Claiming the restored database failed with SQL error {Number}", error.Number);
            throw new BackupException(BackupErrors.Privilege);
        }
    }

    /// <summary>Logs that continuously roll the full forward; to <paramref name="stopAt"/> when given.</summary>
    public static List<BackupPoint> SelectChain(BackupPoint full, IReadOnlyList<BackupPoint> logs, DateTime? stopAt)
    {
        var chain = new List<BackupPoint>();
        if (full.LastLsn is not { } reached)
        {
            if (stopAt is not null) throw new BackupException(BackupErrors.LogChain);
            return chain;
        }
        if (stopAt is { } at && full.SqlFinishedServerTime is { } finished && at < finished) throw new BackupException(BackupErrors.LogChain);
        foreach (var log in logs.OrderBy(l => l.FirstLsn))
        {
            if (log.LastLsn <= reached) continue;
            if (log.FirstLsn > reached)
            {
                // A gap: without a target time stop before it; with one, the target must lie before the gap.
                if (stopAt is null) break;
                throw new BackupException(BackupErrors.LogChain);
            }
            chain.Add(log); reached = log.LastLsn!.Value;
            if (stopAt is { } until && log.SqlFinishedServerTime >= until) return chain;
        }
        if (stopAt is not null && (chain.Count == 0 || chain[^1].SqlFinishedServerTime < stopAt)) throw new BackupException(BackupErrors.LogChain);
        return chain;
    }

    /// <summary>Exactly the files the restored rows reference, verified by hash. Never resurrects erased CVs.</summary>
    private async Task<(int Restored, int Acknowledged)> RebuildCvsAsync(IBackupCloudStore store, BackupSql restored, BackupManifest full,
        List<BackupManifest> logs, string filesRoot, CancellationToken ct)
    {
        var available = new Dictionary<string, (ManifestCv Cv, string KeyId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in logs.AsEnumerable().Reverse().Append(full))
            foreach (var cv in manifest.CvFiles) available.TryAdd(cv.Path + "|" + cv.Sha256, (cv, manifest.KeyId));
        var acknowledged = logs.Append(full).SelectMany(m => m.KnownMissing).Select(m => m.CvId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefix = full.CvStorageRoot.Trim('/') + "/";
        var root = Path.GetFullPath(Path.Combine(filesRoot, full.CvStorageRoot.Replace('/', Path.DirectorySeparatorChar)));
        int count = 0, skipped = 0;
        foreach (var row in await restored.CvRowsAsync(ct))
        {
            var stored = row.StoredPath.Replace('\\', '/');
            if (!stored.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new BackupException(BackupErrors.Compare);
            var relative = stored[prefix.Length..];
            if (!available.TryGetValue(relative + "|" + row.Sha256, out var found))
            {
                if (acknowledged.Contains(row.CvId.ToString())) { skipped++; continue; }
                throw new BackupException(BackupErrors.Dependency);
            }
            var (cv, keyId) = found;
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new BackupException(BackupErrors.Compare);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await DownloadPlainAsync(store, keyId, new ManifestFile(cv.FileId, "", cv.Bytes, cv.Sha256, cv.CipherBytes, cv.CipherSha256, ""), target, ct);
            await using var check = File.OpenRead(target);
            if (check.Length != row.Bytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct)) != row.Sha256) throw new BackupException(BackupErrors.Compare);
            count++;
        }
        return (count, skipped);
    }

    /// <summary>Downloads, checks the ciphertext hash, decrypts and checks the plaintext hash. Deletes partial output on failure.</summary>
    private async Task<string> DownloadPlainAsync(IBackupCloudStore store, string keyId, ManifestFile file, string target, CancellationToken ct)
    {
        using var key = c.Keys.Load(keyId);
        var cipherPath = target + ".enc";
        try
        {
            await using (var output = new FileStream(cipherPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                await store.DownloadAsync(file.FileId, output, ct);
                output.Position = 0;
                if (output.Length != file.CipherBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(output, ct)) != file.CipherSha256)
                    throw new BackupException(BackupErrors.Checksum);
                output.Position = 0;
                await using var plain = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var result = await BackupCrypto.DecryptAsync(key, BackupCrypto.Readable(new NonDisposing(output)), plain, ct);
                if (result.PlainSha256 != file.PlainSha256) throw new BackupException(BackupErrors.Checksum);
            }
            return target;
        }
        catch { if (File.Exists(target)) File.Delete(target); throw; }
        finally { if (File.Exists(cipherPath)) File.Delete(cipherPath); }
    }

    /// <summary>Drops only the database this job just created, recognised by its generated name.</summary>
    private static async Task DropOwnAsync(BackupSql sql, string target, CancellationToken ct)
    {
        if (!BackupLimits.RestoreDatabase.IsMatch(target)) throw new BackupException(BackupErrors.Config);
        await sql.ExecuteAsync($"ALTER DATABASE [{target}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{target}];", BackupErrors.Restore, ct);
    }
}
