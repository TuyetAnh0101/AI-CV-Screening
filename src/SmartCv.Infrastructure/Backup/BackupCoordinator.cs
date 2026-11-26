using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Backup;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Backup;

/// <summary>
/// One tick of one worker role: publish readiness, recover interrupted jobs, schedule what is due (without any
/// Admin online), then run queued jobs one by one. Job state lives only in the database.
/// </summary>
public sealed class BackupCoordinator(BackupEngineContext c)
{
    private static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(30);
    private const int MaxAttempts = 3;
    private string Role => c.Options.Role;
    private IReadOnlyList<string> Types => Role == BackupWorkerRoles.Restore ? BackupJobTypes.RestoreRole : BackupJobTypes.BackupRole;

    public async Task TickAsync(CancellationToken ct)
    {
        // Only one worker per role works at a time, even across machines or double starts.
        await using var gate = await BackupSql.OpenAsync(c.ConnectionString, 60, ct);
        if (!await gate.TryLockAsync("SCApp.Backup.Worker." + Role, "Exclusive", 0, ct)) return;
        var problem = await HeartbeatAsync(ct);
        await RecoverInterruptedAsync(ct);
        if (problem is not BackupErrors.Config) CleanLeftovers();
        if (Role == BackupWorkerRoles.Backup) await ScheduleBackupsAsync(ct);
        else if (problem is null) await ScheduleDrillAsync(ct); // a drill that cannot succeed is not queued
        if (problem is BackupErrors.Config) return;
        while (!ct.IsCancellationRequested && await ClaimAsync(ct) is { } jobId)
        {
            await ExecuteAsync(jobId, ct);
            await HeartbeatAsync(ct);
        }
    }

    // ------------------------------------------------------------------ readiness
    private async Task<string?> HeartbeatAsync(CancellationToken ct)
    {
        string? problem = null; string? keyId = null; DateTime? escrow = null; long? free = null;
        if (c.Options.Validate() is { } invalid) { problem = BackupErrors.Config; c.Logger.LogError("Backup worker configuration invalid: {Setting}", invalid); }
        else
        {
            try { using var key = c.Keys.Load(); keyId = key.KeyId; escrow = c.Keys.Info()?.EscrowConfirmedAtUtc; }
            catch (BackupException error) { problem = error.Code; }
            try
            {
                Directory.CreateDirectory(c.Options.StagingDirectory);
                free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(c.Options.StagingDirectory))!).AvailableFreeSpace;
                if (free < c.Options.MinimumFreeSpaceMb * 1024 * 1024) problem ??= BackupErrors.Space;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { problem ??= BackupErrors.Config; }
        }
        string? warning = null;
        if (problem is not BackupErrors.Config)
        {
            var health = await SelfCheckAsync(ct);
            if (health.Blocking is not null) problem ??= health.Blocking;
            warning = health.Warning;
        }
        await using var db = c.Db();
        if (problem is null && !await db.BackupDriveConnections.AnyAsync(ct)) problem = BackupErrors.DriveNotConnected;
        var heartbeat = await db.BackupWorkerHeartbeats.FindAsync([Role], ct);
        if (heartbeat is null) db.BackupWorkerHeartbeats.Add(heartbeat = new() { Role = Role });
        heartbeat.HeartbeatAtUtc = c.UtcNow; heartbeat.MachineName = Truncate(Environment.MachineName, 64); heartbeat.Version = Truncate(BackupEngineContext.ToolVersion, 40);
        // Ready with a problem code means "can run, but needs attention"; not ready means the code blocks work.
        heartbeat.Ready = problem is null; heartbeat.Problem = problem ?? warning; heartbeat.KeyId = keyId; heartbeat.KeyEscrowConfirmedAtUtc = escrow; heartbeat.StagingFreeBytes = free;
        await db.SaveChangesAsync(ct);
        if (Role == BackupWorkerRoles.Backup) await CheckDriveAsync(db, ct);
        return problem;
    }

    private (DateTime At, string? Blocking, string? Warning)? selfCheck;

    /// <summary>
    /// What only the service itself can know: whether the account it actually runs as has exactly the permissions
    /// it needs, and whether the CV files the database refers to are there. Repeated every five minutes.
    /// </summary>
    private async Task<(string? Blocking, string? Warning)> SelfCheckAsync(CancellationToken ct)
    {
        if (selfCheck is { } recent && c.UtcNow - recent.At < TimeSpan.FromMinutes(5) && recent.Blocking is null) return (recent.Blocking, recent.Warning);
        string? blocking = null, warning = null;
        try
        {
            await using var sql = await BackupSql.OpenAsync(c.ConnectionString, 60, ct);
            var (sufficient, excessive) = await PermissionsAsync(sql, ct);
            if (!sufficient) blocking = BackupErrors.Privilege;
            else if (excessive) warning = BackupErrors.ExcessPrivilege;
            if (sufficient && Role == BackupWorkerRoles.Backup)
            {
                var checks = await CvInventory.CheckAsync(await sql.CvRowsAsync(ct), c.Options.CvBaseDirectory, c.Options.CvStorageRoot,
                    c.Options.KnownMissingCvs, verifyHash: false, ct);
                if (checks.Any(x => x.IsProblem)) warning = BackupErrors.Files;
            }
        }
        catch (BackupException error) { blocking = error.Code; }
        catch (Microsoft.Data.SqlClient.SqlException) { blocking = BackupErrors.SqlConnect; }
        // A full backup packs both key rings last; without this check a missing permission would surface only then.
        if (Role == BackupWorkerRoles.Backup && !KeyRingsReadable()) blocking ??= BackupErrors.Keys;
        selfCheck = (c.UtcNow, blocking, warning);
        return (blocking, warning);
    }

    private bool KeyRingsReadable()
    {
        try
        {
            foreach (var path in new[] { c.Options.ApiKeysPath, c.Options.WebKeysPath })
            {
                var files = Directory.GetFiles(path, "key-*.xml");
                if (files.Length == 0) return false;
                foreach (var file in files) File.OpenRead(file).Dispose(); // contents are not read
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private async Task<(bool Sufficient, bool Excessive)> PermissionsAsync(BackupSql sql, CancellationToken ct)
    {
        async Task<bool> Yes(BackupSql on, string condition) =>
            await on.ScalarAsync($"SELECT CASE WHEN {condition} THEN 1 ELSE 0 END;", ct) is 1;
        var sysadmin = await Yes(sql, "IS_SRVROLEMEMBER('sysadmin')=1");
        if (Role == BackupWorkerRoles.Backup)
        {
            var enough = await Yes(sql, "HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','BACKUP DATABASE')=1 AND HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','BACKUP LOG')=1" +
                " AND HAS_PERMS_BY_NAME('dbo.Cvs','OBJECT','SELECT','FileHash','COLUMN')=1 AND HAS_PERMS_BY_NAME('dbo.BackupJobs','OBJECT','INSERT')=1");
            // Without the backup history a full backup has no LSN and no transaction log can ever follow it.
            try { await sql.ScalarAsync("SELECT TOP 0 bs.first_lsn FROM msdb.dbo.backupset bs JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id=bs.media_set_id;", ct); }
            catch (Microsoft.Data.SqlClient.SqlException) { enough = false; }
            return (enough, sysadmin || await Yes(sql, "HAS_PERMS_BY_NAME(NULL,NULL,'CREATE ANY DATABASE')=1"));
        }
        var canRestore = await Yes(sql, "HAS_PERMS_BY_NAME(NULL,NULL,'CREATE ANY DATABASE')=1 AND HAS_PERMS_BY_NAME('dbo.BackupJobs','OBJECT','UPDATE')=1");
        if (canRestore && !sysadmin)
        {
            await using var master = await BackupSql.OpenAsync(c.ConnectionString, 60, ct, catalog: "master");
            canRestore = await Yes(master, $"HAS_PERMS_BY_NAME('dbo.{BackupGrantScript.ClaimProcedure}','OBJECT','EXECUTE')=1");
        }
        return (canRestore, sysadmin);
    }

    /// <summary>Can the stored credential still reach Drive? Every six hours, every quarter hour while it fails.</summary>
    private async Task CheckDriveAsync(SmartCvDbContext db, CancellationToken ct)
    {
        var connection = await db.BackupDriveConnections.SingleOrDefaultAsync(ct);
        if (connection is null) return;
        var interval = connection.LastCheckOk == false ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(6);
        if (connection.LastCheckedAtUtc > c.UtcNow - interval) return;
        try
        {
            var store = await c.Cloud.OpenAsync(db, ct);
            _ = await store.GetAsync(connection.FolderId, ct) ?? throw new BackupException(BackupErrors.DriveMissing);
            connection.LastCheckOk = true; connection.LastCheckError = null;
        }
        catch (BackupException error) { connection.LastCheckOk = false; connection.LastCheckError = error.Code; }
        // An unexpected answer from Google must not stop the jobs of this tick.
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
        { connection.LastCheckOk = false; connection.LastCheckError = BackupErrors.DriveUnavailable; }
        connection.LastCheckedAtUtc = c.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A worker killed mid-job leaves plaintext behind. This process holds the role lock, so nothing of this role
    /// is running. Transaction logs waiting for upload live elsewhere and are never touched here.
    /// </summary>
    private void CleanLeftovers()
    {
        try
        {
            if (Role == BackupWorkerRoles.Backup)
            {
                if (!Directory.Exists(c.Options.StagingDirectory)) return;
                foreach (var directory in Directory.GetDirectories(c.Options.StagingDirectory, "job-*"))
                    if (Guid.TryParseExact(Path.GetFileName(directory)[4..], "N", out _)) BackupEngine.DeleteDirectory(directory);
            }
            else if (Directory.Exists(c.Options.RestoreFilesRoot))
                foreach (var directory in Directory.GetDirectories(c.Options.RestoreFilesRoot))
                    if (BackupLimits.RestoreDatabase.IsMatch(Path.GetFileName(directory))) RestoreEngine.DeleteTransientFiles(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { c.Logger.LogWarning("Leftover temporary files could not be removed: {Type}", error.GetType().Name); }
    }

    // ------------------------------------------------------------------ recovery and scheduling
    private async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        await using var db = c.Db();
        // This process holds the role lock, so any Running job of the role belongs to a worker that died.
        var types = Types.ToList();
        foreach (var job in await db.BackupJobs.Where(j => j.Status == BackupJobStatuses.Running && types.Contains(j.Type)).ToListAsync(ct))
        {
            job.Status = BackupJobStatuses.Failed; job.ErrorCode = BackupErrors.Interrupted; job.FinishedAtUtc = c.UtcNow;
            await db.SaveChangesAsync(ct);
            await ScheduleRetryAsync(db, job, ct);
            Audit(db, job);
            await db.SaveChangesAsync(ct);
        }
    }

    private DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(c.UtcNow, DateTimeKind.Utc), c.Clock.LocalTimeZone);
    private DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), c.Clock.LocalTimeZone);
    private DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), c.Clock.LocalTimeZone);

    private async Task ScheduleBackupsAsync(CancellationToken ct)
    {
        await using var db = c.Db();
        var settings = await db.BackupSettings.SingleAsync(ct);
        var plan = BackupSchedule.Plan(settings.FullTimeLocal, settings.Paused,
            settings.LastHandledFullSlotUtc is { } handled ? ToLocal(handled) : null, LocalNow, MissedGrace);
        foreach (var slot in plan.MissedLocal)
        {
            var utc = ToUtc(slot);
            if (!await db.BackupJobs.AnyAsync(j => j.Type == BackupJobTypes.Full && j.ScheduledSlotUtc == utc, ct))
                db.BackupJobs.Add(new BackupJob
                {
                    BackupJobId = Guid.NewGuid(), Type = BackupJobTypes.Full, Trigger = BackupTriggers.Scheduled, Status = BackupJobStatuses.Missed,
                    RequestedAtUtc = utc, FinishedAtUtc = utc, ScheduledSlotUtc = utc, ErrorCode = BackupErrors.Missed
                });
        }
        settings.LastHandledFullSlotUtc = ToUtc(plan.HandledLocal);
        await db.SaveChangesAsync(ct);
        if (plan.RunSlotLocal is { } due)
        {
            // A catch-up run is a new job; the slot itself was just recorded as missed.
            var slot = plan.CatchUp ? (DateTime?)null : ToUtc(due);
            await EnqueueAsync(db, BackupJobTypes.Full, plan.CatchUp ? BackupTriggers.CatchUp : BackupTriggers.Scheduled, slot, ct);
        }
        if (settings.Paused) return;
        // Weekly integrity check: download, authenticate and decrypt without anyone asking.
        if (await db.BackupPoints.AnyAsync(p => p.Status != BackupPointStatuses.Deleted && p.DatabaseName == c.Database, ct))
        {
            var verified = await db.BackupJobs.Where(j => j.Type == BackupJobTypes.Verify).OrderByDescending(j => j.RequestedAtUtc).Select(j => (DateTime?)j.RequestedAtUtc).FirstOrDefaultAsync(ct);
            if (verified is null || c.UtcNow - verified >= TimeSpan.FromDays(7)) await EnqueueAsync(db, BackupJobTypes.Verify, BackupTriggers.Scheduled, null, ct);
        }
        var hasAnchor =await db.BackupPoints.AnyAsync(p => p.Kind == BackupPointKinds.Full && p.Status == BackupPointStatuses.Available
            && p.DatabaseName == c.Database && p.LastLsn != null, ct);
        if (!hasAnchor || !await IsFullRecoveryAsync(ct)) return;
        var last = await db.BackupJobs.Where(j => j.Type == BackupJobTypes.Log).OrderByDescending(j => j.RequestedAtUtc).Select(j => (DateTime?)j.RequestedAtUtc).FirstOrDefaultAsync(ct);
        if (last is null || c.UtcNow - last >= TimeSpan.FromHours(settings.LogIntervalHours))
            await EnqueueAsync(db, BackupJobTypes.Log, BackupTriggers.Scheduled, null, ct);
    }

    private async Task<bool> IsFullRecoveryAsync(CancellationToken ct)
    {
        await using var sql = await BackupSql.OpenAsync(c.ConnectionString, 60, ct);
        return (await sql.DatabaseInfoAsync(ct)).RecoveryModel != "SIMPLE";
    }

    /// <summary>Weekly automatic restore drill by the restore role: restore evidence without anyone asking.</summary>
    private async Task ScheduleDrillAsync(CancellationToken ct)
    {
        await using var db = c.Db();
        var settings = await db.BackupSettings.SingleAsync(ct);
        if (settings.Paused || !settings.WeeklyRestoreDrill) return;
        if (!await db.BackupPoints.AnyAsync(p => p.Kind == BackupPointKinds.Full && p.Status == BackupPointStatuses.Available && p.DatabaseName == c.Database, ct)) return;
        var last = await db.BackupJobs.Where(j => j.Type == BackupJobTypes.RestoreDrill).OrderByDescending(j => j.RequestedAtUtc).Select(j => (DateTime?)j.RequestedAtUtc).FirstOrDefaultAsync(ct);
        if (last is null || c.UtcNow - last >= TimeSpan.FromDays(7)) await EnqueueAsync(db, BackupJobTypes.RestoreDrill, BackupTriggers.Scheduled, null, ct);
    }

    private async Task EnqueueAsync(SmartCvDbContext db, string type, string trigger, DateTime? slotUtc, CancellationToken ct,
        int attempt = 1, Guid? retryOf = null, DateTime? notBefore = null)
    {
        if (slotUtc is { } s && await db.BackupJobs.AnyAsync(j => j.Type == type && j.ScheduledSlotUtc == s, ct)) return;
        if (await db.BackupJobs.AnyAsync(j => j.Type == type && (j.Status == BackupJobStatuses.Queued || j.Status == BackupJobStatuses.Running), ct)) return;
        var job = new BackupJob
        {
            BackupJobId = Guid.NewGuid(), Type = type, Trigger = trigger, Status = BackupJobStatuses.Queued, RequestedAtUtc = c.UtcNow,
            ScheduledSlotUtc = slotUtc, Attempt = attempt, RetryOfJobId = retryOf, NotBeforeUtc = notBefore
        };
        db.BackupJobs.Add(job);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.Entry(job).State = EntityState.Detached; } // another process queued it first
    }

    private async Task ScheduleRetryAsync(SmartCvDbContext db, BackupJob failed, CancellationToken ct)
    {
        // Only automatic fulls retry by themselves; manual requests are retried by the Admin.
        if (failed.Type != BackupJobTypes.Full || failed.Trigger == BackupTriggers.Manual || !BackupErrors.IsTransient(failed.ErrorCode) || failed.Attempt >= MaxAttempts) return;
        await EnqueueAsync(db, failed.Type, BackupTriggers.Retry, null, ct, failed.Attempt + 1, failed.BackupJobId, c.UtcNow + RetryDelay);
    }

    // ------------------------------------------------------------------ execution
    private async Task<Guid?> ClaimAsync(CancellationToken ct)
    {
        await using var db = c.Db();
        var now = c.UtcNow;
        var types = Types.ToList();
        var job = await db.BackupJobs.Where(j => j.Status == BackupJobStatuses.Queued && types.Contains(j.Type) && (j.NotBeforeUtc == null || j.NotBeforeUtc <= now))
            .OrderBy(j => j.Type == BackupJobTypes.Full ? 0 : j.Type == BackupJobTypes.Recovery ? 1 : 2).ThenBy(j => j.RequestedAtUtc).FirstOrDefaultAsync(ct);
        if (job is null) return null;
        job.Status = BackupJobStatuses.Running; job.StartedAtUtc = now; job.HeartbeatAtUtc = now; job.WorkerMachine = Truncate(Environment.MachineName, 64);
        await db.SaveChangesAsync(ct);
        return job.BackupJobId;
    }

    private async Task ExecuteAsync(Guid jobId, CancellationToken ct)
    {
        BackupJob job;
        await using (var db = c.Db()) job = await db.BackupJobs.AsNoTracking().SingleAsync(j => j.BackupJobId == jobId, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(c.Options.JobTimeoutMinutes));
        using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = KeepAliveAsync(jobId, beat.Token);
        async Task Progress(string stage, int percent) => await UpdateAsync(jobId, j =>
        {
            j.Stage = Truncate(stage, 60); j.Progress = (byte)Math.Clamp(percent, 0, 100); j.HeartbeatAtUtc = c.UtcNow;
            // Known early, so a worker that dies mid-restore does not leave an unnamed database behind.
            if (job.TargetDatabase is not null) j.TargetDatabase = job.TargetDatabase;
        }, CancellationToken.None);
        string? error = null;
        RestoreEvidence? evidence = null;
        try
        {
            var engine = new BackupEngine(c);
            switch (job.Type)
            {
                case BackupJobTypes.Full: await engine.RunFullAsync(job, Progress, timeout.Token); break;
                case BackupJobTypes.Log: await engine.RunLogAsync(job, Progress, timeout.Token); break;
                case BackupJobTypes.Verify: await engine.RunVerifyAsync(job, Progress, timeout.Token); break;
                case BackupJobTypes.CatalogSync: await engine.RunCatalogSyncAsync(job, Progress, timeout.Token); break;
                case BackupJobTypes.RestoreDrill or BackupJobTypes.Recovery: evidence = await new RestoreEngine(c).RunAsync(job, Progress, timeout.Token); break;
                default: throw new BackupException(BackupErrors.Config);
            }
        }
        catch (BackupException failure) { error = failure.Code; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = BackupErrors.Timeout; }
        catch (OperationCanceledException) { error = BackupErrors.Interrupted; }
        catch (Microsoft.Data.SqlClient.SqlException) { error = BackupErrors.SqlBackup; c.Logger.LogError("Backup job {Job} failed with a SQL error", jobId); }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            error = BackupErrors.Unexpected;
            // Type only: messages may contain paths or connection details.
            c.Logger.LogError("Backup job {Job} failed: {Type}", jobId, failure.GetType().Name);
        }
        await beat.CancelAsync();
        try { await heartbeat; } catch (OperationCanceledException) { }

        await using var context = c.Db();
        var saved = await context.BackupJobs.SingleAsync(j => j.BackupJobId == jobId, CancellationToken.None);
        saved.Status = error is null ? BackupJobStatuses.Succeeded : BackupJobStatuses.Failed;
        saved.ErrorCode = error; saved.FinishedAtUtc = c.UtcNow; saved.Progress = error is null ? (byte)100 : saved.Progress;
        saved.BackupPointId = job.BackupPointId; saved.DatabaseBytes = job.DatabaseBytes; saved.UploadedBytes = job.UploadedBytes;
        saved.CvFiles = job.CvFiles; saved.NewCvFiles = job.NewCvFiles; saved.NewCvBytes = job.NewCvBytes; saved.TargetDatabase = job.TargetDatabase;
        if (job.Type is BackupJobTypes.RestoreDrill or BackupJobTypes.Recovery && job.BackupPointId is { } point
            && await context.BackupPoints.FindAsync([point], CancellationToken.None) is { } restored)
        {
            restored.RestoreTestedAtUtc = c.UtcNow; restored.RestoreTestResult = error ?? "success";
        }
        Audit(context, saved);
        // A Drive failure makes the "last check succeeded" answer stale: check again on the next heartbeat.
        if (Role == BackupWorkerRoles.Backup && error is BackupErrors.DriveAuth or BackupErrors.DriveQuota or BackupErrors.DriveUnavailable
            && await context.BackupDriveConnections.SingleOrDefaultAsync(CancellationToken.None) is { } connection)
            connection.LastCheckedAtUtc = null;
        await context.SaveChangesAsync(CancellationToken.None);
        await ScheduleRetryAsync(context, saved, CancellationToken.None);
        if (error is null) c.Logger.LogInformation("Backup job {Job} ({Type}) succeeded", jobId, job.Type);
        else c.Logger.LogWarning("Backup job {Job} ({Type}) failed: {Code}", jobId, job.Type, error);
        _ = evidence;
    }

    private async Task KeepAliveAsync(Guid jobId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            try
            {
                await UpdateAsync(jobId, j => j.HeartbeatAtUtc = c.UtcNow, ct);
                await using var db = c.Db();
                if (await db.BackupWorkerHeartbeats.FindAsync([Role], ct) is { } worker) { worker.HeartbeatAtUtc = c.UtcNow; await db.SaveChangesAsync(ct); }
            }
            // A brief database hiccup must not turn a finished backup into an "interrupted" one.
            catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
            { c.Logger.LogWarning("Heartbeat update failed: {Type}", error.GetType().Name); }
        }
    }

    private async Task UpdateAsync(Guid jobId, Action<BackupJob> change, CancellationToken ct)
    {
        await using var db = c.Db();
        var job = await db.BackupJobs.SingleAsync(j => j.BackupJobId == jobId, ct);
        change(job);
        await db.SaveChangesAsync(ct);
    }

    private void Audit(SmartCvDbContext db, BackupJob job) => db.AuditEvents.Add(new AuditEvent
    {
        OccurredAtUtc = c.UtcNow, ActionCode = job.Status == BackupJobStatuses.Succeeded ? "backup.job_succeeded" : "backup.job_failed",
        Category = "System", ResourceType = "BackupJob", ResourceId = job.BackupJobId.ToString("D"),
        Outcome = job.Status == BackupJobStatuses.Succeeded ? "Succeeded" : "Failed", RequestId = job.BackupJobId.ToString("N"),
        SafeSummary = Truncate($"Worker sao lưu: {BackupLabels.Type(job.Type)} ({BackupLabels.Trigger(job.Trigger)})", 300), FailureCode = job.ErrorCode
    });

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
