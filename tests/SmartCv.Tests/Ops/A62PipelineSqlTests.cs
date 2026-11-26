using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Backup;
using SmartCv.Infrastructure.Backup;
using SmartCv.Infrastructure.Storage;

namespace SmartCv.Tests.Ops;

public sealed class A62PipelineSqlTests
{
    private static async Task<RestoreEvidence> Evidence(A62Installation it, string database) =>
        JsonSerializer.Deserialize<RestoreEvidence>(await File.ReadAllTextAsync(Path.Combine(it.Restores, database, "restore-result.json")), BackupManifest.Json)!;

    private static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    [A62SqlFact]
    public async Task Schedule_backup_encrypt_upload_verify_restore_and_recover_to_a_point_in_time()
    {
        await using var it = await A62Installation.CreateAsync();

        // Installation tick publishes readiness; nothing is due yet and nothing is invented as missed.
        await it.Tick();
        Assert.Empty(await it.Jobs());
        await using (var db = it.Db()) Assert.True((await db.BackupWorkerHeartbeats.SingleAsync()).Ready);

        // Two nights pass without any Admin: the missed night is recorded and one catch-up run happens.
        await using (var db = it.Db())
        {
            var settings = await db.BackupSettings.SingleAsync();
            settings.LastHandledFullSlotUtc = settings.LastHandledFullSlotUtc!.Value.AddDays(-2);
            await db.SaveChangesAsync();
        }
        await it.Tick();
        var jobs = await it.Jobs();
        Assert.Contains(jobs, j => j.Status == "Missed" && j.ErrorCode == BackupErrors.Missed);
        var full = Assert.Single(jobs, j => j.Type == "full" && j.Status != "Missed");
        Assert.Equal(("Succeeded", "catch-up"), (full.Status, full.Trigger));
        Assert.True(full.CvFiles > 0); Assert.Equal(full.CvFiles, full.NewCvFiles); Assert.True(full.UploadedBytes > 0);
        // The next tick queues the first transaction log by itself.
        await it.Tick();
        Assert.Equal("Succeeded", (await it.Jobs()).Single(j => j.Type == "log").Status);

        // Drive holds only ciphertext: no CV text, no key XML, no manifest fields; staging is empty.
        foreach (var file in it.Cloud.Files.Values)
        {
            foreach (var needle in new[] { "A62 SECRET CONTENT", "<descriptor", "cvFiles", "SmartCv_A62", "%PDF" }) Assert.False(Contains(file.Content, needle), file.Name + " leaks " + needle);
            Assert.EndsWith(".enc", file.Name);
        }
        Assert.Empty(Directory.GetFiles(it.Staging, "*", SearchOption.AllDirectories));

        // A missing CV fails the backup; the previous point stays; after the fix, a retry uploads no CV again.
        var cv = Directory.GetFiles(Path.Combine(it.CvBase, "Uploads"), "*.pdf", SearchOption.AllDirectories)[0];
        File.Move(cv, cv + ".away");
        var failed = await it.Request("full"); await it.Tick();
        Assert.Equal(("Failed", "A62_FILES"), ((await it.Job(failed)).Status, (await it.Job(failed)).ErrorCode));
        Assert.Empty(Directory.GetFiles(it.Staging, "*", SearchOption.AllDirectories));
        File.Move(cv + ".away", cv);
        var retry = await it.Request("full"); await it.Tick();
        var second = await it.Job(retry);
        Assert.Equal("Succeeded", second.Status); Assert.Equal(0, second.NewCvFiles); Assert.Equal(full.CvFiles, second.CvFiles);

        // An erasure holding the A39 lock exclusively blocks the backup instead of being ignored.
        await using (var holder = await BackupSql.OpenAsync(it.Connection, 60, default))
        {
            Assert.True(await holder.TryLockAsync(BackupSql.PrivacyResource, "Exclusive", 0, default));
            var locked = await it.Request("full"); await it.Tick();
            Assert.Equal(BackupErrors.Locked, (await it.Job(locked)).ErrorCode);
        }

        // Transient Drive failure on a scheduled full: one retry is queued for later, not run immediately.
        it.Cloud.FailUpload = name => name.Contains(".db.") ? BackupErrors.DriveUnavailable : null;
        await using (var db = it.Db()) { var s = await db.BackupSettings.SingleAsync(); s.LastHandledFullSlotUtc = s.LastHandledFullSlotUtc!.Value.AddDays(-1); await db.SaveChangesAsync(); }
        it.Clock.Offset = TimeSpan.FromMinutes(1);
        await it.Tick();
        it.Cloud.FailUpload = null;
        var queued = (await it.Jobs()).Single(j => j.Status == "Queued" && j.Type == "full");
        Assert.Equal(("retry", 2), (queued.Trigger, queued.Attempt)); Assert.True(queued.NotBeforeUtc > it.Clock.GetUtcNow().UtcDateTime);
        it.Clock.Offset = TimeSpan.FromMinutes(32);
        await it.Tick();
        Assert.Equal("Succeeded", (await it.Job(queued.BackupJobId)).Status);
        it.Clock.Offset = TimeSpan.Zero;

        // Verify downloads, authenticates and decrypts the newest point.
        var verify = await it.Request("verify"); await it.Tick();
        Assert.Equal("Succeeded", (await it.Job(verify)).Status);

        // Point in time: two logs, restore to the moment between them.
        await it.Upload("uploaded before the chosen point");
        var log1 = await it.Request("log"); await it.Tick(); Assert.Equal("Succeeded", (await it.Job(log1)).Status);
        await Task.Delay(1500);
        var pointInTime = (DateTime)(await it.Execute("SELECT SYSDATETIME()", it.Catalog))!;
        await Task.Delay(1500);
        var later = await it.Upload("uploaded after the chosen point");
        var log2 = await it.Request("log"); await it.Tick(); Assert.Equal("Succeeded", (await it.Job(log2)).Status);
        var total = (int)(await it.Execute("SELECT COUNT(*) FROM dbo.Cvs", it.Catalog))!;

        // Everything a restore must leave alone: the source rows, the database itself and the CV files.
        async Task<string> Source()
        {
            var state = new StringBuilder();
            foreach (var table in new[] { "Users", "Workspaces", "WorkspaceMembers", "Jobs", "Cvs", "Applications", "Evaluations" })
                state.Append(table).Append('=').Append(await it.Execute($"SELECT CONCAT(COUNT_BIG(*),':',COALESCE(CHECKSUM_AGG(BINARY_CHECKSUM(*)),0)) FROM dbo.[{table}]", it.Catalog)).Append(';');
            state.Append(await it.Execute($"SELECT CONCAT(database_id,':',CONVERT(varchar(33),create_date,126),':',state_desc) FROM sys.databases WHERE name='{it.Catalog}'"));
            foreach (var file in Directory.GetFiles(it.CvBase, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                state.Append(';').Append(Path.GetFileName(file)).Append(':').Append(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))));
            return state.ToString();
        }
        var sourceBeforeRestores = await Source();

        // Restore drill by the restore role: restores from the cloud copy, checks, drops its own database.
        await it.Tick(BackupWorkerRoles.Restore);
        var drill = await it.Request("restore-drill"); await it.Tick(BackupWorkerRoles.Restore);
        var drillJob = await it.Job(drill);
        Assert.True(drillJob.Status == "Succeeded", drillJob.ErrorCode); Assert.Null(drillJob.TargetDatabase);
        Assert.Empty(Directory.GetDirectories(it.Restores));
        Assert.Equal(DBNull.Value, await it.Execute("SELECT MAX(database_id) FROM sys.databases WHERE name LIKE 'SmartCv[_]Restore[_]%' AND create_date > DATEADD(minute,-1,GETDATE())"));
        Guid anchor;
        await using (var db = it.Db())
        {
            anchor = drillJob.BackupPointId!.Value;
            Assert.Equal("success", (await db.BackupPoints.SingleAsync(p => p.BackupPointId == anchor)).RestoreTestResult);
        }

        // Controlled recovery to the point in time: new database, files rebuilt and hash-checked, keys restored.
        var recovery = await it.Request("recovery", anchor, pointInTime); await it.Tick(BackupWorkerRoles.Restore);
        var recoveryJob = await it.Job(recovery);
        Assert.True(recoveryJob.Status == "Succeeded", recoveryJob.ErrorCode);
        var database = recoveryJob.TargetDatabase!;
        Assert.Matches("^SmartCv_Restore_[a-f0-9]{32}$", database);
        var evidence = await Evidence(it, database);
        Assert.True(evidence.CheckDbPassed); Assert.True(evidence.KeyRingsRestored); Assert.Equal(2, evidence.LogsApplied); // STOPAT lies inside the second log
        Assert.Equal(total - 1, evidence.TableRows["Cvs"]);
        Assert.Equal(DBNull.Value, await it.Execute($"SELECT MAX(CvId) FROM dbo.Cvs WHERE CvId='{later}'", database) ?? DBNull.Value);
        var storage = new LocalCvFileStorage(Path.Combine(it.Restores, database, "files"), "Uploads/CV");
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(it.Server.ConnectionString) { InitialCatalog = database }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new Microsoft.Data.SqlClient.SqlCommand("SELECT StoredPath,FileHash FROM dbo.Cvs", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var checkedFiles = 0;
            while (await reader.ReadAsync())
            {
                using var content = storage.OpenRead(reader.GetString(0));
                Assert.Equal(reader.GetString(1), Convert.ToHexStringLower(SHA256.HashData(content)), ignoreCase: true); checkedFiles++;
            }
            Assert.Equal(evidence.CvFilesRestored, checkedFiles);
        }
        // Key tables match the source at that point.
        Assert.Equal(await it.Execute("SELECT COUNT(*) FROM dbo.Users", it.Catalog), (int)evidence.TableRows["Users"]);
        Assert.Equal(await it.Execute("SELECT COUNT(*) FROM dbo.Workspaces", it.Catalog), (int)evidence.TableRows["Workspaces"]);

        // After a drill and a recovery the source is byte for byte what it was.
        Assert.Equal(sourceBeforeRestores, await Source());

        // A lost cloud object stops a restore before any database is created, and verify marks the point damaged.
        string lostId;
        await using (var db = it.Db())
        {
            var objectKey = (await db.BackupPointObjects.FirstAsync(o => o.BackupPointId == anchor)).ObjectKey;
            lostId = (await db.BackupObjects.SingleAsync(o => o.ObjectKey == objectKey)).FileId;
        }
        var lost = it.Cloud.Files[lostId];
        it.Cloud.Files.TryRemove(lostId, out _);
        var broken = await it.Request("recovery", anchor); await it.Tick(BackupWorkerRoles.Restore);
        Assert.Equal(BackupErrors.DriveMissing, (await it.Job(broken)).ErrorCode);
        var check = await it.Request("verify"); await it.Tick();
        Assert.Equal("Failed", (await it.Job(check)).Status);
        await using (var db = it.Db()) Assert.True(await db.BackupPoints.AnyAsync(p => p.Status == "Damaged"));
        it.Cloud.Files[lostId] = lost;
        var again = await it.Request("verify"); await it.Tick();
        Assert.Equal("Succeeded", (await it.Job(again)).Status);
        await using (var db = it.Db()) Assert.False(await db.BackupPoints.AnyAsync(p => p.Status == "Damaged"));

        // Someone else's log backup breaks the chain: reported, and the log is still stored.
        await it.Upload("before a foreign log backup");
        await it.Execute($"BACKUP LOG [{it.Catalog}] TO DISK = N'NUL';");
        await it.Upload("after a foreign log backup");
        var gap = await it.Request("log"); await it.Tick();
        Assert.Equal(BackupErrors.LogChain, (await it.Job(gap)).ErrorCode);
        Assert.NotNull((await it.Job(gap)).BackupPointId);

        // A Drive outage during a log backup keeps the encrypted log locally and publishes it next time.
        var renew = await it.Request("full"); await it.Tick(); Assert.Equal("Succeeded", (await it.Job(renew)).Status);
        it.Cloud.FailUpload = _ => BackupErrors.DriveUnavailable;
        await it.Upload("during the outage");
        var outage = await it.Request("log"); await it.Tick();
        Assert.Equal(BackupErrors.DriveUnavailable, (await it.Job(outage)).ErrorCode);
        var pending = Directory.GetFiles(Path.Combine(it.Staging, "logs-pending"), "log.trn.enc", SearchOption.AllDirectories);
        Assert.Single(pending); Assert.Empty(Directory.GetFiles(it.Staging, "log.trn", SearchOption.AllDirectories));
        it.Cloud.FailUpload = null;
        int logsBefore; await using (var db = it.Db()) logsBefore = await db.BackupPoints.CountAsync(p => p.Kind == "Log");
        var recoverOutage = await it.Request("log"); await it.Tick(); Assert.Equal("Succeeded", (await it.Job(recoverOutage)).Status);
        await using (var db = it.Db()) Assert.Equal(logsBefore + 2, await db.BackupPoints.CountAsync(p => p.Kind == "Log"));

        // A CV problem right after BACKUP LOG: the log is still encrypted and stored, never left in plaintext.
        var missing = Directory.GetFiles(Path.Combine(it.CvBase, "Uploads"), "*.pdf", SearchOption.AllDirectories)[0];
        File.Move(missing, missing + ".away");
        var damagedLog = await it.Request("log"); await it.Tick();
        Assert.Equal(BackupErrors.Files, (await it.Job(damagedLog)).ErrorCode);
        Assert.NotNull((await it.Job(damagedLog)).BackupPointId);
        Assert.Empty(Directory.GetFiles(it.Staging, "log.trn*", SearchOption.AllDirectories));
        File.Move(missing + ".away", missing);

        // Retention keeps N full points and every object they still need.
        await using (var db = it.Db()) { var s = await db.BackupSettings.SingleAsync(); s.RetentionCount = 7; await db.SaveChangesAsync(); }
        for (var i = 0; i < 4; i++) { var run = await it.Request("full"); await it.Tick(); Assert.Equal("Succeeded", (await it.Job(run)).Status); }
        await using (var db = it.Db())
        {
            Assert.Equal(7, await db.BackupPoints.CountAsync(p => p.Kind == "Full" && p.Status == "Available"));
            Assert.True(await db.BackupPoints.AnyAsync(p => p.Status == "Deleted"));
            var live = await db.BackupPointObjects.Where(o => db.BackupPoints.Any(p => p.BackupPointId == o.BackupPointId && p.Status == "Available"))
                .Join(db.BackupObjects, o => o.ObjectKey, b => b.ObjectKey, (o, b) => b).ToListAsync();
            Assert.All(live, o => { Assert.Null(o.DeletedAtUtc); Assert.True(it.Cloud.Files.ContainsKey(o.FileId)); });
            foreach (var gone in await db.BackupPoints.Where(p => p.Status == "Deleted").ToListAsync()) Assert.False(it.Cloud.Files.ContainsKey(gone.PayloadFileId));
        }

        // The catalog can be rebuilt from the manifests on Drive.
        Guid forgotten;
        await using (var db = it.Db())
        {
            var point = await db.BackupPoints.Where(p => p.Kind == "Full" && p.Status == "Available").OrderByDescending(p => p.CreatedAtUtc).FirstAsync();
            forgotten = point.BackupPointId;
            await db.BackupPointObjects.Where(o => o.BackupPointId == forgotten).ExecuteDeleteAsync();
            db.BackupPoints.Remove(point); await db.SaveChangesAsync();
        }
        var sync = await it.Request("catalog-sync"); await it.Tick();
        Assert.Equal("Succeeded", (await it.Job(sync)).Status);
        await using (var db = it.Db()) Assert.True(await db.BackupPoints.AnyAsync(p => p.BackupPointId == forgotten));

        // A worker that died mid-job is reported as failed, never silently successful.
        await using (var db = it.Db())
        {
            db.BackupJobs.Add(new() { BackupJobId = Guid.NewGuid(), Type = "verify", Trigger = "manual", Status = "Running", RequestedAtUtc = DateTime.UtcNow, StartedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await it.Tick();
        Assert.Contains(await it.Jobs(), j => j.ErrorCode == BackupErrors.Interrupted && j.Status == "Failed");

        // Audit trail written by the worker; logs carry no paths, SQL text or CV content.
        Assert.True((int)(await it.Execute("SELECT COUNT(*) FROM dbo.AuditEvents WHERE ActionCode IN ('backup.job_succeeded','backup.job_failed')", it.Catalog))! > 5);
        Assert.DoesNotContain(it.Log.Lines, line => line.Contains(it.Root, StringComparison.OrdinalIgnoreCase) || line.Contains("SECRET", StringComparison.Ordinal));
    }
}
