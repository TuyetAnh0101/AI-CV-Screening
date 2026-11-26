using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Backup;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Backup;

public delegate Task ProgressReporter(string stage, int percent);

/// <summary>Everything a backup or restore step needs. Created once per worker process.</summary>
public sealed class BackupEngineContext(BackupWorkerOptions options, string connectionString, Func<SmartCvDbContext> db,
    IBackupKeyProvider keys, IBackupCloudStoreFactory cloud, TimeProvider clock, ILogger logger)
{
    public BackupWorkerOptions Options { get; } = options;
    public string ConnectionString { get; } = connectionString;
    public string Database { get; } = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
    public Func<SmartCvDbContext> Db { get; } = db;
    public IBackupKeyProvider Keys { get; } = keys;
    public IBackupCloudStoreFactory Cloud { get; } = cloud;
    public TimeProvider Clock { get; } = clock;
    public ILogger Logger { get; } = logger;
    public DateTime UtcNow => Clock.GetUtcNow().UtcDateTime;
    public string CvRoot => CvInventory.Root(Options.CvBaseDirectory, Options.CvStorageRoot);
    public string PendingLogs => Path.Combine(Options.StagingDirectory, "logs-pending");
    public string JobStaging(Guid job) => Path.Combine(Options.StagingDirectory, "job-" + job.ToString("N"));
    public static string ToolVersion => typeof(BackupEngine).Assembly.GetName().Version?.ToString() ?? "0";

    public async Task<IBackupCloudStore> OpenStoreAsync(CancellationToken ct)
    {
        await using var context = Db();
        return await Cloud.OpenAsync(context, ct);
    }

    /// <summary>Refuses work that would exceed the staging cap or leave less than the reserve on the volume.</summary>
    public void EnsureSpace(string directory, long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory))!;
        var free = new DriveInfo(root).AvailableFreeSpace;
        long used = 0;
        if (Directory.Exists(Options.StagingDirectory))
            foreach (var file in Directory.EnumerateFiles(Options.StagingDirectory, "*", SearchOption.AllDirectories)) used += new FileInfo(file).Length;
        if (free - bytes < Options.MinimumFreeSpaceMb * 1024 * 1024 || (directory.StartsWith(Options.StagingDirectory, StringComparison.OrdinalIgnoreCase)
            && used + bytes > Options.StagingLimitMb * 1024 * 1024)) throw new BackupException(BackupErrors.Space);
    }
}

public sealed record StagedObject(string ObjectKey, string LocalPath, long PlainBytes, BackupCipherResult Cipher);
public sealed record CvSnapshot(IReadOnlyList<(CvRow Row, string Path)> Files, IReadOnlyList<ManifestKnownMissing> KnownMissing, int Orphans)
{
    /// <summary>Referenced rows that are missing, altered or unreadable. A full backup refuses any.</summary>
    public int Problems { get; init; }
}

/// <summary>Backup-role pipelines: full, transaction log, verify, catalog sync, retention.</summary>
public sealed class BackupEngine(BackupEngineContext c)
{
    private static readonly IReadOnlyDictionary<string, string> NoProperties = new Dictionary<string, string>();

    // ------------------------------------------------------------------ full
    public async Task RunFullAsync(BackupJob job, ProgressReporter progress, CancellationToken ct)
    {
        // A log still waiting from an outage is published first; if it cannot be, the full backup still runs.
        try { await PublishPendingLogsAsync(ct); }
        catch (BackupException error) { c.Logger.LogWarning("A transaction log is still waiting for upload: {Code}", error.Code); }
        using var key = c.Keys.Load();
        var store = await c.OpenStoreAsync(ct);
        var staging = c.JobStaging(job.BackupJobId);
        Directory.CreateDirectory(staging);
        try
        {
            await progress("prepare", 5);
            // What Drive really holds: a CV object deleted or damaged there is uploaded again instead of being trusted.
            var onDrive = (await store.ListAsync("scapp-cv-", ct)).ToDictionary(f => f.Id, f => f.Size, StringComparer.Ordinal);
            await using var sql = await BackupSql.OpenAsync(c.ConnectionString, c.Options.CommandTimeoutSeconds, ct);
            if (!await sql.TryLockAsync("SCApp.Backup." + c.Database, "Exclusive", 0, ct)) throw new BackupException(BackupErrors.Locked);
            var info = await sql.DatabaseInfoAsync(ct);
            c.EnsureSpace(staging, await sql.AllocatedBytesAsync(ct) * 2);
            var point = Guid.NewGuid();
            var created = c.UtcNow;
            var bak = Path.Combine(staging, "database.bak");
            SqlBackupLsn? lsn; CvSnapshot snapshot; List<StagedObject> staged;
            await progress("cv-lock", 10);
            await sql.EnterPrivacyAsync(c.Options.PrivacyLockSeconds, ct);
            try
            {
                AssertNoPendingErasure();
                var before = await sql.CvRowsAsync(ct);
                await progress("sql-backup", 20);
                await sql.ExecuteAsync("BACKUP DATABASE @db TO DISK=@path WITH COPY_ONLY, CHECKSUM" + info.CompressionClause + ";", BackupErrors.SqlBackup, ct,
                    ("@db", c.Database), ("@path", bak));
                lsn = await sql.BackupLsnAsync(bak, ct);
                // Rows are never deleted or rewritten while the lock is held, so the list read AFTER the backup
                // covers every row inside it. Files are copied (encrypted) before the lock is released.
                await progress("cv-snapshot", 35);
                var after = await sql.CvRowsAsync(ct);
                AssertPreserved(before, after);
                snapshot = await SnapshotAsync(after, ct);
                if (snapshot.Problems > 0) throw new BackupException(BackupErrors.Files);
                staged = await StageObjectsAsync(key, snapshot, staging, onDrive, ct);
                await sql.AssertPrivacyHeldAsync(ct);
            }
            finally { await sql.ReleaseAsync(BackupSql.PrivacyResource, CancellationToken.None); }

            await progress("encrypt", 50);
            var payload = await EncryptFileAsync(key, bak, bak + ".enc", ct);
            File.Delete(bak);
            var keysArchive = Path.Combine(staging, "keys.zip");
            PackKeyRings(keysArchive);
            var keysCipher = await EncryptFileAsync(key, keysArchive, keysArchive + ".enc", ct);
            File.Delete(keysArchive);

            await progress("upload", 60);
            var properties = new Dictionary<string, string> { ["scapp"] = "full", ["point"] = point.ToString("N") };
            var uploaded = 0L;
            var payloadFile = await UploadVerifiedAsync(store, bak + ".enc", BackupManifest.FileName("full", created, point, "db"), payload, properties, ct);
            uploaded += payload.CipherBytes;
            var keysFile = await UploadVerifiedAsync(store, keysArchive + ".enc", BackupManifest.FileName("full", created, point, "keys"), keysCipher, properties, ct);
            uploaded += keysCipher.CipherBytes;
            var objects = await UploadObjectsAsync(store, staged, (done, total) => progress("upload", 60 + 30 * done / Math.Max(1, total)), ct);
            uploaded += staged.Sum(s => s.Cipher.CipherBytes);

            await progress("manifest", 92);
            await using (var db = c.Db())
            {
                var manifest = new BackupManifest(1, BackupManifest.Producer, point, BackupPointKinds.Full, c.Database, created, key.KeyId, "online-privacy-locked",
                    await sql.SchemaVersionAsync(ct), c.Options.CvStorageRoot.Replace('\\', '/').Trim('/'),
                    new(Lsn(lsn?.First), Lsn(lsn?.Last), lsn?.FinishedServerTime, info.Edition, info.Version, info.RecoveryModel, true),
                    Describe(payloadFile, payload), Describe(keysFile, keysCipher), await DescribeCvsAsync(db, key, snapshot, objects, requireAll: true, ct),
                    snapshot.KnownMissing, snapshot.Orphans, BackupEngineContext.ToolVersion);
                var manifestFile = await UploadManifestAsync(store, key, manifest, staging, properties, ct);
                await RecordPointAsync(db, manifest, manifestFile, job, ct);
                await AuditKnownMissingAsync(db, manifest, job, ct);
            }
            job.BackupPointId = point; job.DatabaseBytes = payload.PlainBytes; job.UploadedBytes = uploaded;
            job.CvFiles = snapshot.Files.Count; job.NewCvFiles = staged.Count; job.NewCvBytes = staged.Sum(s => s.PlainBytes);

            await progress("retention", 96);
            await ApplyRetentionAsync(store, ct);
        }
        finally { DeleteDirectory(staging); }
    }

    // ------------------------------------------------------------------ transaction log
    /// <summary>
    /// A normal BACKUP LOG: afterwards SQL Server may reuse that log space, so the file becomes the only copy of
    /// those transactions. It stays (encrypted) in logs-pending until Drive has it and the catalog row is committed.
    /// </summary>
    public async Task RunLogAsync(BackupJob job, ProgressReporter progress, CancellationToken ct)
    {
        await PublishPendingLogsAsync(ct);
        using var key = c.Keys.Load();
        BackupPoint? anchor;
        await using (var db = c.Db())
            anchor = await db.BackupPoints.Where(p => p.Kind == BackupPointKinds.Full && p.Status == BackupPointStatuses.Available && p.DatabaseName == c.Database && p.LastLsn != null)
                .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (anchor is null) throw new BackupException(BackupErrors.NoBackup);
        // Fail before truncating the log when Drive is not usable at all: this really calls Google.
        _ = await (await c.OpenStoreAsync(ct)).ListAsync("scapp-log-", ct);

        await progress("prepare", 5);
        await using var sql = await BackupSql.OpenAsync(c.ConnectionString, c.Options.CommandTimeoutSeconds, ct);
        if (!await sql.TryLockAsync("SCApp.Backup." + c.Database, "Exclusive", 0, ct)) throw new BackupException(BackupErrors.Locked);
        var info = await sql.DatabaseInfoAsync(ct);
        if (info.RecoveryModel == "SIMPLE") throw new BackupException(BackupErrors.RecoveryModel);
        var point = Guid.NewGuid();
        var directory = Path.Combine(c.PendingLogs, point.ToString("N"));
        c.EnsureSpace(c.Options.StagingDirectory, await sql.LogUsedBytesAsync(ct) * 2);
        Directory.CreateDirectory(directory);
        var trn = Path.Combine(directory, "log.trn");
        await progress("cv-lock", 10);
        await sql.EnterPrivacyAsync(c.Options.PrivacyLockSeconds, ct);
        PendingLog pending;
        BackupException? cvProblem = null;
        try
        {
            AssertNoPendingErasure();
            var before = await sql.CvRowsAsync(ct);
            await progress("sql-backup", 20);
            await sql.ExecuteAsync("BACKUP LOG @db TO DISK=@path WITH CHECKSUM" + info.CompressionClause + ";", BackupErrors.SqlBackup, ct, ("@db", c.Database), ("@path", trn));
            var lsn = await sql.BackupLsnAsync(trn, ct) ?? throw new BackupException(BackupErrors.LogChain);
            pending = new PendingLog(point, job.BackupJobId, c.UtcNow, Lsn(lsn.First)!, Lsn(lsn.Last)!, lsn.FinishedServerTime, info.Edition, info.Version,
                info.RecoveryModel, await sql.SchemaVersionAsync(ct), null, [], [], 0);
            await WritePendingAsync(directory, pending, ct);
            await progress("cv-snapshot", 35);
            // The log is now the only copy of those transactions: a CV problem is reported AFTER it is safely stored.
            try
            {
                var after = await sql.CvRowsAsync(ct);
                AssertPreserved(before, after);
                // One broken old file must not stop the new uploads from being captured with this log.
                var snapshot = await SnapshotAsync(after, ct);
                await StageObjectsAsync(key, snapshot, directory, null, ct);
                await sql.AssertPrivacyHeldAsync(ct);
                pending = pending with
                {
                    Files = snapshot.Files.Select(f => new PendingCv(f.Path, f.Row.Bytes, f.Row.Sha256)).ToList(),
                    KnownMissing = snapshot.KnownMissing.ToList(), Orphans = snapshot.Orphans
                };
                if (snapshot.Problems > 0) cvProblem = new BackupException(BackupErrors.Files);
            }
            catch (BackupException error)
            {
                cvProblem = error;
                foreach (var partial in Directory.GetFiles(directory, "cv-*")) File.Delete(partial);
            }
        }
        finally { await sql.ReleaseAsync(BackupSql.PrivacyResource, CancellationToken.None); }

        await progress("encrypt", 50);
        var cipher = await EncryptFileAsync(key, trn, trn + ".enc", ct);
        // Prove the encrypted copy decrypts to the same bytes before the only plaintext copy is removed.
        await using (var check = File.OpenRead(trn + ".enc"))
        {
            var back = await BackupCrypto.DecryptAsync(key, BackupCrypto.Readable(check), Stream.Null, ct);
            if (back.PlainSha256 != cipher.PlainSha256) throw new BackupException(BackupErrors.Checksum);
        }
        pending = pending with { Payload = cipher };
        await WritePendingAsync(directory, pending, ct);
        File.Delete(trn);
        job.BackupPointId = point; job.DatabaseBytes = cipher.PlainBytes;

        await progress("upload", 60);
        var (uploaded, newFiles, newBytes, chainOk) = await PublishAsync(key, directory, pending, progress, ct);
        job.UploadedBytes = uploaded; job.NewCvFiles = newFiles; job.NewCvBytes = newBytes; job.CvFiles = pending.Files.Count;
        if (cvProblem is not null) throw cvProblem;
        // The log is safely stored either way; a gap only means point-in-time restore cannot cross it.
        if (!chainOk) throw new BackupException(BackupErrors.LogChain);
    }

    private sealed record PendingCv(string Path, long Bytes, string Sha256);
    private sealed record PendingLog(Guid PointId, Guid JobId, DateTime CreatedAtUtc, string FirstLsn, string LastLsn, DateTime FinishedServerTime,
        string Edition, string Version, string RecoveryModel, string? SchemaVersion, BackupCipherResult? Payload, List<PendingCv> Files,
        List<ManifestKnownMissing> KnownMissing, int Orphans);

    /// <summary>Uploads every log still waiting from an earlier run. Never deletes one that is not yet recorded.</summary>
    public async Task PublishPendingLogsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(c.PendingLogs)) return;
        foreach (var directory in Directory.GetDirectories(c.PendingLogs).Order(StringComparer.Ordinal))
        {
            var pending = await ReadPendingAsync(directory, ct) ?? await RecoverOrphanLogAsync(directory, ct);
            if (pending is null) continue;
            var trn = Path.Combine(directory, "log.trn");
            var encrypted = trn + ".enc";
            // An already encrypted log keeps the key it was written with, even after a rotation.
            string? keyId = null;
            if (pending.Payload is not null && File.Exists(encrypted))
            {
                await using var header = File.OpenRead(encrypted);
                keyId = await BackupCrypto.ReadKeyIdAsync(header, ct);
            }
            using var key = keyId is null ? c.Keys.Load() : c.Keys.Load(keyId);
            if (pending.Payload is null)
            {
                // Crashed between BACKUP LOG and encryption: the plaintext is still the only copy.
                if (!File.Exists(trn)) continue;
                if (File.Exists(encrypted)) File.Delete(encrypted); // half written by the interrupted run
                var cipher = await EncryptFileAsync(key, trn, encrypted, ct);
                await using (var check = File.OpenRead(encrypted))
                    if ((await BackupCrypto.DecryptAsync(key, BackupCrypto.Readable(check), Stream.Null, ct)).PlainSha256 != cipher.PlainSha256)
                        throw new BackupException(BackupErrors.Checksum);
                pending = pending with { Payload = cipher };
                await WritePendingAsync(directory, pending, ct);
            }
            if (File.Exists(trn)) File.Delete(trn);
            await PublishAsync(key, directory, pending, (_, _) => Task.CompletedTask, ct);
        }
    }

    /// <summary>Replaced in one step, so a crash never leaves a half-written descriptor next to the only log copy.</summary>
    private static async Task WritePendingAsync(string directory, PendingLog pending, CancellationToken ct)
    {
        var path = Path.Combine(directory, "pending.json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(pending, BackupManifest.Json), ct);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static async Task<PendingLog?> ReadPendingAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, "pending.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<PendingLog>(await File.ReadAllTextAsync(path, ct), BackupManifest.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The worker died after BACKUP LOG but before describing the file. SQL Server's history says whether that
    /// file is a complete log backup; if so it is published, otherwise it is kept and reported, never deleted.
    /// </summary>
    private async Task<PendingLog?> RecoverOrphanLogAsync(string directory, CancellationToken ct)
    {
        var trn = Path.Combine(directory, "log.trn");
        if (!File.Exists(trn))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) DeleteDirectory(directory);
            return null;
        }
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var point)) return null;
        await using var sql = await BackupSql.OpenAsync(c.ConnectionString, c.Options.CommandTimeoutSeconds, ct);
        var lsn = await sql.BackupLsnAsync(trn, ct);
        if (lsn is null)
        {
            c.Logger.LogError("A transaction log file in staging has no readable SQL backup history; it is kept for manual review.");
            return null;
        }
        var info = await sql.DatabaseInfoAsync(ct);
        var pending = new PendingLog(point, Guid.Empty, File.GetLastWriteTimeUtc(trn), Lsn(lsn.First)!, Lsn(lsn.Last)!, lsn.FinishedServerTime,
            info.Edition, info.Version, info.RecoveryModel, null, null, [], [], 0);
        await WritePendingAsync(directory, pending, ct);
        return pending;
    }

    private async Task<(long Uploaded, int NewFiles, long NewBytes, bool ChainOk)> PublishAsync(BackupKey key, string directory, PendingLog pending,
        ProgressReporter progress, CancellationToken ct)
    {
        await using var db = c.Db();
        if (await db.BackupPoints.AnyAsync(p => p.BackupPointId == pending.PointId, ct)) { DeleteDirectory(directory); return (0, 0, 0, true); }
        var store = await c.Cloud.OpenAsync(db, ct);
        var properties = new Dictionary<string, string> { ["scapp"] = "log", ["point"] = pending.PointId.ToString("N") };
        var payload = await UploadVerifiedAsync(store, Path.Combine(directory, "log.trn.enc"),
            BackupManifest.FileName("log", pending.CreatedAtUtc, pending.PointId, "log"), pending.Payload!, properties, ct);
        var staged = new List<StagedObject>();
        foreach (var local in Directory.GetFiles(directory, "cv-*.enc"))
        {
            // Without its description the copy was interrupted mid-write; the CV itself is still in storage.
            if (!File.Exists(local + ".json")) { File.Delete(local); continue; }
            var meta = JsonSerializer.Deserialize<StagedObject>(await File.ReadAllTextAsync(local + ".json", ct), BackupManifest.Json)!;
            staged.Add(meta with { LocalPath = local });
        }
        var objects = await UploadObjectsAsync(store, staged, (done, total) => progress("upload", 60 + 30 * done / Math.Max(1, total)), ct);
        var snapshot = new CvSnapshot(pending.Files.Select(f => (new CvRow(Guid.Empty, f.Path, f.Sha256, f.Bytes), f.Path)).ToList(), pending.KnownMissing, pending.Orphans);
        var manifest = new BackupManifest(1, BackupManifest.Producer, pending.PointId, BackupPointKinds.Log, c.Database, pending.CreatedAtUtc, key.KeyId,
            "online-privacy-locked", pending.SchemaVersion, c.Options.CvStorageRoot.Replace('\\', '/').Trim('/'),
            new(pending.FirstLsn, pending.LastLsn, pending.FinishedServerTime, pending.Edition, pending.Version, pending.RecoveryModel, false),
            Describe(payload, pending.Payload!), null, await DescribeCvsAsync(db, key, snapshot, objects, requireAll: false, ct), pending.KnownMissing, pending.Orphans,
            BackupEngineContext.ToolVersion);
        var manifestFile = await UploadManifestAsync(store, key, manifest, directory, properties, ct);
        var job = await db.BackupJobs.FindAsync([pending.JobId], ct);
        var first = decimal.Parse(pending.FirstLsn, CultureInfo.InvariantCulture);
        var previous = await db.BackupPoints.Where(p => p.Kind == BackupPointKinds.Log && p.DatabaseName == c.Database && p.Status != BackupPointStatuses.Deleted && p.LastLsn != null)
            .OrderByDescending(p => p.LastLsn).Select(p => p.LastLsn).FirstOrDefaultAsync(ct);
        var anchor = await db.BackupPoints.Where(p => p.Kind == BackupPointKinds.Full && p.DatabaseName == c.Database && p.Status == BackupPointStatuses.Available && p.LastLsn != null)
            .OrderByDescending(p => p.CreatedAtUtc).Select(p => p.LastLsn).FirstOrDefaultAsync(ct);
        // Continuous when it starts at (or before) the end of the previous log, or covers the newest full backup.
        var chainOk = previous is { } last ? first <= last : anchor is { } full && first <= full;
        await RecordPointAsync(db, manifest, manifestFile, job, ct);
        DeleteDirectory(directory);
        return (pending.Payload!.CipherBytes + staged.Sum(s => s.Cipher.CipherBytes), staged.Count, staged.Sum(s => s.PlainBytes), chainOk);
    }

    // ------------------------------------------------------------------ verify
    /// <summary>
    /// Every available point: files still exist on Drive with the recorded size. The newest full point and the
    /// logs after it: downloaded, authenticated and decrypted end to end. Restore is proven separately by a drill.
    /// </summary>
    public async Task RunVerifyAsync(BackupJob job, ProgressReporter progress, CancellationToken ct)
    {
        await using var db = c.Db();
        var store = await c.Cloud.OpenAsync(db, ct);
        var points = await db.BackupPoints.Where(p => p.Status != BackupPointStatuses.Deleted && p.DatabaseName == c.Database).OrderByDescending(p => p.CreatedAtUtc).ToListAsync(ct);
        if (points.Count == 0) throw new BackupException(BackupErrors.NoBackup);
        var newestFull = points.FirstOrDefault(p => p.Kind == BackupPointKinds.Full && p.Status == BackupPointStatuses.Available);
        string? failure = null;
        var index = 0;
        foreach (var point in points)
        {
            await progress("verify-cloud", 5 + 80 * index++ / points.Count);
            string? result = null;
            try
            {
                foreach (var (id, size) in new[] { (point.ManifestFileId, point.ManifestCipherBytes), (point.PayloadFileId, point.PayloadCipherBytes), (point.KeysFileId, point.KeysCipherBytes ?? 0) })
                {
                    if (id is null or "") continue;
                    var file = await store.GetAsync(id, ct) ?? throw new BackupException(BackupErrors.DriveMissing);
                    if (file.Size != size) throw new BackupException(BackupErrors.DriveVerify);
                }
                var deep = point == newestFull || (point.Kind == BackupPointKinds.Log && newestFull is not null && point.CreatedAtUtc > newestFull.CreatedAtUtc);
                var manifest = await BackupManifest.DownloadAsync(store, c.Keys, point.ManifestFileId, ct);
                if (manifest.PointId != point.BackupPointId) throw new BackupException(BackupErrors.Checksum);
                if (deep)
                {
                    using var key = c.Keys.Load(manifest.KeyId);
                    await DownloadCheckAsync(store, key, manifest.Payload, ct);
                    if (manifest.Keys is { } keys) await DownloadCheckAsync(store, key, keys, ct);
                    foreach (var cv in manifest.CvFiles.DistinctBy(x => x.ObjectKey))
                    {
                        var plain = await DownloadCheckAsync(store, key, new ManifestFile(cv.FileId, "", cv.Bytes, cv.Sha256, cv.CipherBytes, cv.CipherSha256, ""), ct);
                        if (plain != cv.Sha256) throw new BackupException(BackupErrors.Checksum);
                    }
                }
                else foreach (var cv in manifest.CvFiles.DistinctBy(x => x.ObjectKey))
                    if ((await store.GetAsync(cv.FileId, ct))?.Size != cv.CipherBytes) throw new BackupException(BackupErrors.DriveMissing);
            }
            catch (BackupException error) when (error.Code is BackupErrors.DriveMissing or BackupErrors.DriveVerify or BackupErrors.Checksum or BackupErrors.KeyMismatch)
            {
                result = error.Code; failure ??= error.Code;
            }
            point.VerifiedAtUtc = c.UtcNow; point.VerifyResult = result ?? "success";
            // A point whose files are back and verified again becomes usable again. A key this machine does not hold
            // says nothing about the files: marking it damaged would let retention delete a good backup.
            if (result != BackupErrors.KeyMismatch) point.Status = result is null ? BackupPointStatuses.Available : BackupPointStatuses.Damaged;
            await db.SaveChangesAsync(ct);
        }
        await progress("done", 100);
        if (failure is not null) throw new BackupException(failure);
    }

    private static async Task<string> DownloadCheckAsync(IBackupCloudStore store, BackupKey key, ManifestFile file, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "scapp-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous))
            {
                await store.DownloadAsync(file.FileId, output, ct);
                output.Position = 0;
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(output, ct)) != file.CipherSha256) throw new BackupException(BackupErrors.Checksum);
                output.Position = 0;
                var result = await BackupCrypto.DecryptAsync(key, BackupCrypto.Readable(new NonDisposing(output)), Stream.Null, ct);
                if (file.PlainSha256.Length > 0 && result.PlainSha256 != file.PlainSha256) throw new BackupException(BackupErrors.Checksum);
                return result.PlainSha256;
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ------------------------------------------------------------------ catalog sync
    /// <summary>Rebuilds missing catalog rows from the manifests on Drive (e.g. after a disaster recovery).</summary>
    public async Task RunCatalogSyncAsync(BackupJob job, ProgressReporter progress, CancellationToken ct)
    {
        using (c.Keys.Load()) { } // no key, no catalog
        await using var db = c.Db();
        var store = await c.Cloud.OpenAsync(db, ct);
        var manifests = await store.ListAsync(".manifest.enc", ct);
        var known = (await db.BackupPoints.Select(p => p.ManifestFileId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var foreign = false; var index = 0; var imported = 0; var otherDatabase = 0;
        foreach (var file in manifests.Where(m => !known.Contains(m.Id)))
        {
            await progress("catalog", 10 + 80 * index++ / Math.Max(1, manifests.Count));
            BackupManifest manifest;
            try { manifest = await BackupManifest.DownloadAsync(store, c.Keys, file.Id, ct); }
            catch (BackupException error) when (error.Code is BackupErrors.KeyMismatch or BackupErrors.Checksum) { foreign = true; continue; }
            if (manifest.Database != c.Database) { otherDatabase++; continue; }
            if (await db.BackupPoints.AnyAsync(p => p.BackupPointId == manifest.PointId, ct)) continue;
            foreach (var cv in manifest.CvFiles.DistinctBy(x => x.ObjectKey))
                if (await db.BackupObjects.FindAsync([cv.ObjectKey], ct) is null)
                    db.BackupObjects.Add(new() { ObjectKey = cv.ObjectKey, FileId = cv.FileId, PlainBytes = cv.Bytes, CipherBytes = cv.CipherBytes, CipherSha256 = cv.CipherSha256, CreatedAtUtc = manifest.CreatedAtUtc });
            await db.SaveChangesAsync(ct);
            await RecordPointAsync(db, manifest, file, null, ct);
            imported++;
        }
        if (otherDatabase > 0) c.Logger.LogWarning("{Count} backups on Drive belong to a database with another name and were not imported", otherDatabase);
        if (foreign) throw new BackupException(BackupErrors.KeyMismatch);
        // Recovering onto a differently named database must not look like "there was nothing to recover".
        if (otherDatabase > 0 && imported == 0 && known.Count == 0) throw new BackupException(BackupErrors.Database);
    }

    // ------------------------------------------------------------------ retention
    /// <summary>
    /// Keeps the newest N full points, every log a kept full may need, and every CV object a kept point lists.
    /// Drive deletions that fail leave the row in place for the next run. Points rebuilt from Drive (no job) are
    /// never deleted automatically: after a disaster the new, empty installation must not age out the real ones.
    /// </summary>
    public async Task ApplyRetentionAsync(IBackupCloudStore store, CancellationToken ct)
    {
        await using var db = c.Db();
        // A restore in progress may be reading any point: delete nothing until it finishes.
        if (await db.BackupJobs.AnyAsync(j => (j.Type == BackupJobTypes.RestoreDrill || j.Type == BackupJobTypes.Recovery)
            && (j.Status == BackupJobStatuses.Queued || j.Status == BackupJobStatuses.Running), ct)) return;
        var keep = (await db.BackupSettings.SingleAsync(ct)).RetentionCount;
        var fulls = await db.BackupPoints.Where(p => p.Kind == BackupPointKinds.Full && p.Status == BackupPointStatuses.Available && p.DatabaseName == c.Database && p.BackupJobId != null)
            .OrderByDescending(p => p.CreatedAtUtc).ToListAsync(ct);
        if (fulls.Count == 0) return;
        var drop = fulls.Skip(keep).ToList();
        var oldest = fulls.Take(keep).Last();
        if (oldest.FirstLsn is { } from)
        {
            var logs = await db.BackupPoints.Where(p => p.Kind == BackupPointKinds.Log && p.Status != BackupPointStatuses.Deleted && p.DatabaseName == c.Database && p.LastLsn != null && p.BackupJobId != null)
                .OrderBy(p => p.LastLsn).ToListAsync(ct);
            // Never the newest log; never one that ends after the oldest kept full began.
            drop.AddRange(logs.Take(Math.Max(0, logs.Count - 1)).Where(l => l.LastLsn <= from));
        }
        drop.AddRange(await db.BackupPoints.Where(p => p.Status == BackupPointStatuses.Damaged && p.CreatedAtUtc < oldest.CreatedAtUtc && p.DatabaseName == c.Database && p.BackupJobId != null).ToListAsync(ct));
        foreach (var point in drop.DistinctBy(p => p.BackupPointId))
        {
            try
            {
                foreach (var id in new[] { point.PayloadFileId, point.KeysFileId, point.ManifestFileId }) if (id is { Length: > 0 }) await store.DeleteAsync(id, ct);
                point.Status = BackupPointStatuses.Deleted; point.DeletedAtUtc = c.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            catch (BackupException error) { c.Logger.LogWarning("Retention kept point {Point}: {Code}", point.BackupPointId, error.Code); }
        }
        var live = db.BackupPointObjects.Where(o => db.BackupPoints.Any(p => p.BackupPointId == o.BackupPointId && p.Status != BackupPointStatuses.Deleted)).Select(o => o.ObjectKey);
        foreach (var unused in await db.BackupObjects.Where(o => o.DeletedAtUtc == null && !live.Contains(o.ObjectKey)).ToListAsync(ct))
        {
            try { await store.DeleteAsync(unused.FileId, ct); unused.DeletedAtUtc = c.UtcNow; await db.SaveChangesAsync(ct); }
            catch (BackupException error) { c.Logger.LogWarning("Retention kept object: {Code}", error.Code); }
        }
    }

    // ------------------------------------------------------------------ shared steps
    private void AssertNoPendingErasure()
    {
        var journal = Path.Combine(c.CvRoot, ".a39-recovery");
        if (Directory.Exists(journal) && Directory.EnumerateFileSystemEntries(journal).Any()) throw new BackupException(BackupErrors.Consistency);
    }

    private static void AssertPreserved(List<CvRow> before, List<CvRow> after)
    {
        var now = after.ToDictionary(r => r.CvId);
        foreach (var row in before)
            if (!now.TryGetValue(row.CvId, out var still) || still.StoredPath != row.StoredPath || still.Sha256 != row.Sha256) throw new BackupException(BackupErrors.Consistency);
    }

    /// <summary>
    /// Every referenced file must exist with the recorded size and hash, except exactly acknowledged rows. The hash
    /// is checked on every run, also for content already on Drive: silent damage on disk is reported the same day.
    /// </summary>
    private async Task<CvSnapshot> SnapshotAsync(List<CvRow> rows, CancellationToken ct)
    {
        var checks = await CvInventory.CheckAsync(rows, c.Options.CvBaseDirectory, c.Options.CvStorageRoot, c.Options.KnownMissingCvs, verifyHash: true, ct);
        var files = checks.Where(x => x.State == CvFileState.Ok).Select(x => (x.Row, Path: x.Relative)).ToList();
        var missing = checks.Where(x => x.State == CvFileState.AcknowledgedMissing).Select(x => new ManifestKnownMissing(x.Row.CvId.ToString(), x.Row.Sha256,
            KnownMissingRules.DemoRows.ContainsKey(x.Row.CvId.ToString()) ? KnownMissingRules.DemoSeed : KnownMissingRules.AcceptedLoss)).ToList();
        var problems = checks.Count(x => x.IsProblem);
        // Counts only: file names and ids stay on the server (the cv-report command lists them there).
        if (problems > 0) c.Logger.LogWarning("CV check: {Missing} missing, {Altered} altered, {Other} unusable of {Total} referenced files",
            checks.Count(x => x.State == CvFileState.Missing), checks.Count(x => x.State is CvFileState.SizeMismatch or CvFileState.HashMismatch),
            checks.Count(x => x.State is CvFileState.UnsafePath or CvFileState.Unreadable), checks.Count);
        var seen = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = Directory.Exists(c.CvRoot)
            ? Directory.EnumerateFiles(c.CvRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(c.CvRoot, f).Replace('\\', '/')).Count(f => !seen.Contains(f) && !f.StartsWith(".", StringComparison.Ordinal))
            : 0;
        return new(files, missing, orphans) { Problems = problems };
    }

    /// <summary>
    /// Encrypts each CV not yet on Drive into staging, proving its hash matches the database while reading it.
    /// With <paramref name="onDrive"/> a recorded object only counts when Drive still has it at the recorded size.
    /// </summary>
    private async Task<List<StagedObject>> StageObjectsAsync(BackupKey key, CvSnapshot snapshot, string directory,
        IReadOnlyDictionary<string, long>? onDrive, CancellationToken ct)
    {
        await using var db = c.Db();
        var recorded = await db.BackupObjects.Where(o => o.DeletedAtUtc == null).Select(o => new { o.ObjectKey, o.FileId, o.CipherBytes }).ToListAsync(ct);
        var stored = recorded.Where(o => onDrive is null || (onDrive.TryGetValue(o.FileId, out var size) && size == o.CipherBytes))
            .Select(o => o.ObjectKey).ToHashSet(StringComparer.Ordinal);
        var staged = new List<StagedObject>();
        foreach (var (row, relative) in snapshot.Files.DistinctBy(f => f.Row.Sha256))
        {
            var objectKey = key.ObjectKey(row.Sha256);
            if (stored.Contains(objectKey)) continue;
            var target = Path.Combine(directory, "cv-" + objectKey + ".enc");
            var cipher = await EncryptFileAsync(key, Path.Combine(c.CvRoot, relative.Replace('/', Path.DirectorySeparatorChar)), target, ct);
            if (cipher.PlainSha256 != row.Sha256 || cipher.PlainBytes != row.Bytes) throw new BackupException(BackupErrors.Files);
            var item = new StagedObject(objectKey, target, cipher.PlainBytes, cipher);
            await File.WriteAllTextAsync(target + ".json", JsonSerializer.Serialize(item, BackupManifest.Json), ct);
            staged.Add(item);
        }
        return staged;
    }

    private async Task<Dictionary<string, CloudFile>> UploadObjectsAsync(IBackupCloudStore store, List<StagedObject> staged, Func<int, int, Task> progress, CancellationToken ct)
    {
        var result = new Dictionary<string, CloudFile>(StringComparer.Ordinal);
        var done = 0;
        foreach (var item in staged)
        {
            await using (var db = c.Db())
            {
                // The row may exist already: an earlier attempt uploaded it, retention removed it, or Drive lost it.
                var row = await db.BackupObjects.FindAsync([item.ObjectKey], ct);
                if (row is { DeletedAtUtc: null } && await store.GetAsync(row.FileId, ct) is { } present && present.Size == row.CipherBytes)
                {
                    result[item.ObjectKey] = present;
                }
                else
                {
                    var file = await UploadVerifiedAsync(store, item.LocalPath, BackupManifest.ObjectName(item.ObjectKey), item.Cipher,
                        new Dictionary<string, string> { ["scapp"] = "cv" }, ct);
                    if (row is null) db.BackupObjects.Add(row = new() { ObjectKey = item.ObjectKey });
                    row.FileId = file.Id; row.PlainBytes = item.PlainBytes; row.CipherBytes = item.Cipher.CipherBytes;
                    row.CipherSha256 = item.Cipher.CipherSha256; row.CreatedAtUtc = c.UtcNow; row.DeletedAtUtc = null;
                    await db.SaveChangesAsync(ct);
                    result[item.ObjectKey] = file;
                }
            }
            await progress(++done, staged.Count);
        }
        return result;
    }

    private async Task<List<ManifestCv>> DescribeCvsAsync(SmartCvDbContext db, BackupKey key, CvSnapshot snapshot, Dictionary<string, CloudFile> uploaded,
        bool requireAll, CancellationToken ct)
    {
        var keys = snapshot.Files.Select(f => key.ObjectKey(f.Row.Sha256)).Distinct().ToList();
        var stored = await db.BackupObjects.Where(o => keys.Contains(o.ObjectKey) && o.DeletedAtUtc == null).ToDictionaryAsync(o => o.ObjectKey, ct);
        var result = new List<ManifestCv>();
        foreach (var f in snapshot.Files)
        {
            var objectKey = key.ObjectKey(f.Row.Sha256);
            if (!stored.TryGetValue(objectKey, out var item))
            {
                // A delayed log must still be published; a restore finds the file in another point or refuses.
                if (requireAll) throw new BackupException(BackupErrors.Dependency);
                c.Logger.LogWarning("A delayed transaction log is published without one CV that is no longer stored");
                continue;
            }
            result.Add(new ManifestCv(f.Path, f.Row.Bytes, f.Row.Sha256, objectKey, item.FileId, item.CipherBytes, item.CipherSha256));
        }
        return result;
    }

    /// <summary>A backup that relied on acknowledged missing files says so in the audit trail.</summary>
    private async Task AuditKnownMissingAsync(SmartCvDbContext db, BackupManifest manifest, BackupJob job, CancellationToken ct)
    {
        if (manifest.KnownMissing.Count == 0) return;
        var demo = manifest.KnownMissing.Count(m => m.Reason == KnownMissingRules.DemoSeed);
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAtUtc = c.UtcNow, ActionCode = "backup.known_missing_applied", Category = "System", ResourceType = "BackupPoint",
            ResourceId = manifest.PointId.ToString("D"), Outcome = "Succeeded", RequestId = job.BackupJobId.ToString("N"),
            SafeSummary = $"Bản sao lưu bỏ qua {manifest.KnownMissing.Count} hồ sơ thiếu tệp đã được phê duyệt (dữ liệu mẫu: {demo}, mất mát được chấp nhận: {manifest.KnownMissing.Count - demo})"
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<CloudFile> UploadManifestAsync(IBackupCloudStore store, BackupKey key, BackupManifest manifest, string directory,
        IReadOnlyDictionary<string, string> properties, CancellationToken ct)
    {
        var path = Path.Combine(directory, "manifest.json.enc");
        BackupCipherResult cipher;
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            cipher = await BackupCrypto.EncryptAsync(key, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, BackupManifest.Json)), output, ct);
        return await UploadVerifiedAsync(store, path, BackupManifest.FileName(manifest.Kind, manifest.CreatedAtUtc, manifest.PointId, "manifest"), cipher, properties, ct);
    }

    private async Task RecordPointAsync(SmartCvDbContext db, BackupManifest manifest, CloudFile manifestFile, BackupJob? job, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.BackupPoints.Add(new BackupPoint
        {
            BackupPointId = manifest.PointId, Kind = manifest.Kind, Status = BackupPointStatuses.Available, DatabaseName = manifest.Database,
            CreatedAtUtc = manifest.CreatedAtUtc, BackupJobId = job?.BackupJobId, FirstLsn = ParseLsn(manifest.Sql.FirstLsn), LastLsn = ParseLsn(manifest.Sql.LastLsn),
            SqlFinishedServerTime = manifest.Sql.FinishedServerTime, KeyId = manifest.KeyId, ManifestFileId = manifestFile.Id, ManifestCipherBytes = manifestFile.Size,
            PayloadFileId = manifest.Payload.FileId, PayloadPlainBytes = manifest.Payload.PlainBytes, PayloadCipherBytes = manifest.Payload.CipherBytes,
            PayloadCipherSha256 = manifest.Payload.CipherSha256, KeysFileId = manifest.Keys?.FileId, KeysCipherBytes = manifest.Keys?.CipherBytes,
            CvFiles = manifest.CvFiles.Count, CvBytes = manifest.CvFiles.Sum(f => f.Bytes), KnownMissingCvs = manifest.KnownMissing.Count,
            Consistency = manifest.Consistency, SchemaVersion = manifest.AppSchemaVersion
        });
        await db.SaveChangesAsync(ct);
        foreach (var objectKey in manifest.CvFiles.Select(f => f.ObjectKey).Distinct())
            db.BackupPointObjects.Add(new() { BackupPointId = manifest.PointId, ObjectKey = objectKey });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private void PackKeyRings(string archive)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var (prefix, directory) in new[] { ("api", c.Options.ApiKeysPath), ("web", c.Options.WebKeysPath) })
        {
            string[] keys;
            try { keys = Directory.GetFiles(directory, "key-*.xml"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new BackupException(BackupErrors.Keys); }
            if (keys.Length == 0) throw new BackupException(BackupErrors.Keys);
            foreach (var file in keys)
            {
                try { zip.CreateEntryFromFile(file, prefix + "/" + Path.GetFileName(file), CompressionLevel.Optimal); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new BackupException(BackupErrors.Keys); }
            }
        }
    }

    public static async Task<BackupCipherResult> EncryptFileAsync(BackupKey key, string source, string target, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        return await BackupCrypto.EncryptAsync(key, input, output, ct);
    }

    public static async Task<CloudFile> UploadVerifiedAsync(IBackupCloudStore store, string path, string name, BackupCipherResult cipher,
        IReadOnlyDictionary<string, string> properties, CancellationToken ct)
    {
        CloudFile file;
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            file = await store.UploadAsync(name, input, input.Length, properties, ct);
        // Drive computes MD5 of what it stored: anything else means the upload was not what we encrypted.
        if (file.Size != cipher.CipherBytes || !string.Equals(file.Md5, cipher.CipherMd5, StringComparison.OrdinalIgnoreCase))
        {
            await store.DeleteAsync(file.Id, CancellationToken.None);
            throw new BackupException(BackupErrors.DriveVerify);
        }
        return file;
    }

    private static ManifestFile Describe(CloudFile file, BackupCipherResult cipher) =>
        new(file.Id, file.Name, cipher.PlainBytes, cipher.PlainSha256, cipher.CipherBytes, cipher.CipherSha256, cipher.CipherMd5);
    private static string? Lsn(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
    public static decimal? ParseLsn(string? value) => decimal.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var lsn) ? lsn : null;

    public static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Lets a caller keep a stream open after a wrapper is disposed.</summary>
public sealed class NonDisposing(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
}
