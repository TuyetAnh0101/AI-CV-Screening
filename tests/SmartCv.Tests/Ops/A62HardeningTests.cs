extern alias web;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Backup;
using SmartCv.Infrastructure.Backup;
using SmartCv.Infrastructure.Data;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests.Ops;

/// <summary>Where a CV file is, and what the backup may conclude from that. No database.</summary>
public sealed class A62CvInventoryTests
{
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task Every_way_a_referenced_cv_can_be_wrong_is_told_apart()
    {
        var root = Path.Combine(Path.GetTempPath(), "SmartCv_A62_inv_" + Guid.NewGuid().ToString("N"));
        var storage = Path.Combine(root, "Uploads", "CV");
        Directory.CreateDirectory(storage);
        try
        {
            var good = "good content"u8.ToArray();
            CvRow Row(string stored, byte[]? content = null) => new(Guid.NewGuid(), stored, Sha(content ?? good), (content ?? good).Length);
            await File.WriteAllBytesAsync(Path.Combine(storage, "ok.pdf"), good);
            await File.WriteAllBytesAsync(Path.Combine(storage, "short.pdf"), good[..4]);
            await File.WriteAllBytesAsync(Path.Combine(storage, "altered.pdf"), "GOOD content"u8.ToArray());
            // A real file just outside the storage root must never be accepted, whatever the row says.
            await File.WriteAllBytesAsync(Path.Combine(root, "outside.pdf"), good);

            var ok = Row("Uploads/CV/ok.pdf");
            var missing = Row("Uploads/CV/gone.pdf");
            var acknowledged = Row("Uploads/CV/accepted.pdf");
            var rows = new[]
            {
                ok, missing, acknowledged, Row("Uploads/CV/short.pdf"), Row("Uploads/CV/altered.pdf"),
                Row("Other/ok.pdf"), Row("Uploads/CV/../../outside.pdf"), Row("Uploads/CV/C:/Windows/win.ini")
            };
            var known = new[] { new KnownMissingCv(acknowledged.CvId.ToString(), acknowledged.Sha256, KnownMissingRules.AcceptedLoss, "owner", "2026-11-26") };

            async Task<List<CvFileState>> States(bool hash) =>
                (await CvInventory.CheckAsync(rows, root, "Uploads/CV", known, hash, default)).Select(x => x.State).ToList();
            Assert.Equal([CvFileState.Ok, CvFileState.Missing, CvFileState.AcknowledgedMissing, CvFileState.SizeMismatch, CvFileState.HashMismatch,
                CvFileState.UnsafePath, CvFileState.UnsafePath, CvFileState.UnsafePath], await States(true));
            // The quick check compares presence and size only; it must not claim the content was proven.
            Assert.Equal(CvFileState.Ok, (await States(false))[4]);

            // The acknowledged file reappears: it is checked like any other again.
            await File.WriteAllBytesAsync(Path.Combine(storage, "accepted.pdf"), good);
            Assert.Equal(CvFileState.Ok, (await States(true))[2]);
            await File.WriteAllBytesAsync(Path.Combine(storage, "accepted.pdf"), "GOOD content"u8.ToArray());
            Assert.Equal(CvFileState.HashMismatch, (await States(true))[2]);

            // An acknowledgement for different content does not cover this row.
            var stale = new[] { known[0] with { Sha256 = new string('a', 64) } };
            File.Delete(Path.Combine(storage, "accepted.pdf"));
            Assert.Equal(CvFileState.Missing, (await CvInventory.CheckAsync(rows, root, "Uploads/CV", stale, true, default))[2].State);

            // The same relative path under another base directory is a different place.
            Assert.Equal(CvFileState.Missing, (await CvInventory.CheckAsync([ok], storage, "Uploads/CV", [], true, default))[0].State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_file_the_account_may_not_read_is_not_reported_as_lost_or_healthy()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "SmartCv_A62_acl_" + Guid.NewGuid().ToString("N"));
        var hidden = Path.Combine(root, "Uploads", "CV", "hidden");
        Directory.CreateDirectory(hidden);
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        void Icacls(params string[] arguments)
        {
            var start = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd(); process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            var content = "private"u8.ToArray();
            var open = Path.Combine(root, "Uploads", "CV", "locked.pdf");
            await File.WriteAllBytesAsync(open, content);
            await File.WriteAllBytesAsync(Path.Combine(hidden, "inside.pdf"), content);
            var rows = new[] { "Uploads/CV/locked.pdf", "Uploads/CV/hidden/inside.pdf" }
                .Select(path => new CvRow(Guid.NewGuid(), path, Sha(content), content.Length)).ToArray();
            Icacls(open, "/deny", me + ":(R)");
            Icacls(hidden, "/deny", me + ":(OI)(CI)(RX)");
            foreach (var hash in new[] { true, false })
                Assert.All(await CvInventory.CheckAsync(rows, root, "Uploads/CV", [], hash, default), check => Assert.Equal(CvFileState.Unreadable, check.State));
        }
        finally
        {
            Icacls(root, "/reset", "/T", "/Q");
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Catalog_listing_finds_manifests_although_Drive_only_matches_name_prefixes()
    {
        var google = new FakeGoogleHandler();
        var store = new GoogleDriveStore(new HttpClient(google), new("client", "secret", google.RefreshToken), "folder-x");
        foreach (var name in new[] { "scapp-full-1.db.enc", "scapp-full-1.manifest.enc", "scapp-log-2.manifest.enc", "scapp-cv-abc.enc" })
            await store.UploadAsync(name, new MemoryStream([1, 2, 3]), 3, new Dictionary<string, string>(), default);
        Assert.Equal(["scapp-full-1.manifest.enc", "scapp-log-2.manifest.enc"], (await store.ListAsync(".manifest.enc", default)).Select(f => f.Name).Order().ToArray());
        Assert.Single(await store.ListAsync("scapp-cv-", default));
    }

    [Fact]
    public void Rotating_the_key_keeps_the_old_one_readable_and_nothing_else()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = TestSupport.TempRoot();
        try
        {
            var provider = new DpapiBackupKeyProvider(directory);
            var first = provider.Create(RandomNumberGenerator.GetBytes(32), true);
            var bytes = RandomNumberGenerator.GetBytes(32);
            var second = provider.Rotate(bytes, true);
            Assert.NotEqual(first, second);
            using (var current = provider.Load()) Assert.Equal(second, current.KeyId);
            using (var old = provider.Load(first)) Assert.Equal(first, old.KeyId);
            Assert.Equal([first], provider.RetiredKeyIds());
            Assert.Equal(second, provider.Info()!.KeyId);
            Assert.Throws<BackupException>(() => provider.Rotate(bytes, true));
            foreach (var wrong in new[] { new string('0', 32), "..\\..\\x" })
                Assert.Equal(BackupErrors.KeyMismatch, Assert.Throws<BackupException>(() => provider.Load(wrong)).Code);
            // On another machine: import the current key, then the retired one for older points.
            var fresh = new DpapiBackupKeyProvider(Path.Combine(directory, "new-machine"));
            fresh.Create(bytes, true);
            Assert.Throws<BackupException>(() => fresh.Load(first));
            using (var old = provider.Load(first)) Assert.Equal(first, old.KeyId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Connected_running_ready_backed_up_verified_and_drilled_are_separate_facts()
    {
        var nothing = BackupText.Steps(new BackupOverviewDto());
        Assert.Equal(["drive", "running", "ready", "backup", "verified", "drill"], nothing.Select(s => s.Code).ToArray());
        Assert.Equal(["critical", "critical", "critical", "none", "none", "none"], nothing.Select(s => s.State).ToArray());
        var now = DateTime.UtcNow;
        var worker = new BackupWorkerDto(true, true, true, null, now, "k", now, 1);
        var connected = new BackupOverviewDto
        {
            BackupWorker = worker, RestoreWorker = worker with { Ready = false, Problem = BackupErrors.Privilege },
            Drive = new(true, true, "a@example.test", "f", now, now, true, null, null, null)
        };
        Assert.Equal(["ok", "ok", "warning", "none", "none", "none"], BackupText.Steps(connected).Select(s => s.State).ToArray());
        var backedUp = connected with { LastFull = new(Guid.NewGuid(), "Full", "Available", now, now, 1, 1, 1, 0, "k", "c", null, null, null, null) };
        Assert.Equal(["ok", "none", "none"], BackupText.Steps(backedUp).Skip(3).Select(s => s.State).ToArray());
        Assert.Equal(BackupText.Health.Warning, BackupText.Headline(backedUp).Health);
    }
}

/// <summary>The installer's fragile parts, executed by Windows PowerShell itself without administrator rights.</summary>
public sealed class A62InstallerTests
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);

    private static string[] Split(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static string PowerShell(string script)
    {
        var functions = TestSupport.RepoFile("scripts/backup-worker/InstallerFunctions.ps1");
        var full = $"$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest; . '{functions}'; {script}";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(full)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);
        Assert.True(process.ExitCode == 0, errors.Result);
        return output.Trim();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("config")]
    public void Service_binary_path_with_spaces_reaches_sc_as_one_value(string verb)
    {
        if (!OperatingSystem.IsWindows()) return;
        var arguments = PowerShell($"Get-ServiceArguments {verb} SCAppBackupWorker (Get-WorkerBinPath 'C:\\Program Files\\SCApp\\Worker.exe' 'C:\\Program Data\\x y.json') 'NT SERVICE\\SCAppBackupWorker'");
        var parsed = Split("sc.exe " + arguments);
        Assert.Equal(["sc.exe", verb, "SCAppBackupWorker", "binPath=",
            "\"C:\\Program Files\\SCApp\\Worker.exe\" run --config \"C:\\Program Data\\x y.json\"",
            "obj=", "NT SERVICE\\SCAppBackupWorker", "start=", "delayed-auto", "DisplayName=", "SCAppBackupWorker"], parsed);
    }

    [Fact]
    public void Reinstall_leaves_the_logon_alone_and_keeps_the_installed_paths()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parsed = Split("sc.exe " + PowerShell("Get-ServiceArguments config SCAppBackupWorker (Get-WorkerBinPath 'C:\\a b\\w.exe' 'C:\\c.json') ''"));
        Assert.DoesNotContain("obj=", parsed);
        Assert.Equal("\"C:\\a b\\w.exe\" run --config \"C:\\c.json\"", parsed[4]);
        Assert.Equal(@"D:\cv|True|True", PowerShell("$c = '{\"BackupWorker\":{\"CvBaseDirectory\":\"D:\\\\cv\"}}' | ConvertFrom-Json; " +
            "(Get-InstalledValue $c 'BackupWorker' 'CvBaseDirectory') + '|' + ($null -eq (Get-InstalledValue $c 'BackupWorker' 'WebKeysPath')) + '|' + ($null -eq (Get-InstalledValue $null 'a' 'b'))"));
    }

    [Fact]
    public void A_failing_sc_command_is_reported_with_its_exit_code_and_message()
    {
        if (!OperatingSystem.IsWindows()) return;
        var message = PowerShell("try { Invoke-Sc '/c echo access-denied-text & exit 5' (Join-Path $env:WINDIR 'System32\\cmd.exe'); 'no error' } catch { $_.Exception.Message }");
        Assert.Contains("exit code 5", message);
        Assert.Contains("access-denied-text", message);
    }

    [Fact]
    public void Acknowledged_missing_list_keeps_its_shape_and_survives_reinstall()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = TestSupport.TempRoot();
        try
        {
            var list = Path.Combine(directory, "known.json");
            File.WriteAllText(list, """[{"CvId":"a"},{"CvId":"b"}]""");
            string Ids(string json) => string.Join(',', JsonDocument.Parse(json).RootElement.GetProperty("KnownMissingCvs").EnumerateArray().Select(e => e.GetProperty("CvId").GetString()));
            Assert.Equal("a,b", Ids(PowerShell($"@{{ KnownMissingCvs = (Read-KnownMissingList '{list}') }} | ConvertTo-Json -Depth 6 -Compress")));
            var config = Path.Combine(directory, "worker.json");
            // Includes the broken shape the earlier installer wrote.
            File.WriteAllText(config, """{"BackupWorker":{"KnownMissingCvs":[{"value":[{"CvId":"c"}],"Count":1}]}}""");
            Assert.Equal("c", Ids(PowerShell($"@{{ KnownMissingCvs = (Get-ExistingKnownMissing '{config}') }} | ConvertTo-Json -Depth 6 -Compress")));
            Assert.Equal("0", PowerShell($"(Get-ExistingKnownMissing '{Path.Combine(directory, "absent.json")}').Count"));
            File.WriteAllText(list, "[]");
            Assert.Equal("0", PowerShell($"(Read-KnownMissingList '{list}').Count"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Grant_script_stays_least_privilege_and_the_claim_is_opt_in()
    {
        var plain = BackupGrantScript.Build("SmartCvDb", @"NT SERVICE\SCAppBackupWorker", @"NT SERVICE\SCAppRestoreWorker", restoreClaim: false);
        foreach (var forbidden in new[] { "sysadmin", "CONTROL SERVER", "db_owner", "dbcreator", "CERTIFICATE" }) Assert.DoesNotContain(forbidden, plain);
        var claim = BackupGrantScript.Build("SmartCvDb", @"NT SERVICE\SCAppBackupWorker", @"NT SERVICE\SCAppRestoreWorker", restoreClaim: true);
        Assert.Contains("REMOVE PRIVATE KEY", claim);
        Assert.DoesNotContain("ALTER SERVER ROLE", claim);
        // CONTROL SERVER goes to the certificate login only, never to a service account.
        Assert.Single(claim.Split('\n'), line => line.Contains("CONTROL SERVER"));
        Assert.Contains("GRANT CONTROL SERVER TO SCAppRestoreClaimSigner;", claim);
        Assert.All(BackupGrantScript.Batches(claim), batch => Assert.DoesNotContain("\nGO", batch));
    }
}

public sealed class A62HardeningSqlTests
{
    private static async Task<SmartCv.Core.Entities.BackupJob> Run(A62Installation it, string type, Guid? point = null, string role = BackupWorkerRoles.Backup,
        Action<BackupWorkerOptions>? change = null)
    {
        var id = await it.Request(type, point);
        await it.Tick(role, change);
        return await it.Job(id);
    }

    private static async Task<SmartCv.Core.Entities.BackupJob> Succeeds(A62Installation it, string type, Guid? point = null, string role = BackupWorkerRoles.Backup,
        Action<BackupWorkerOptions>? change = null)
    {
        var job = await Run(it, type, point, role, change);
        Assert.True(job.Status == "Succeeded", $"{type}: {job.ErrorCode} at {job.Stage}");
        return job;
    }

    private static string[] CvFiles(A62Installation it) =>
        Directory.GetFiles(Path.Combine(it.CvBase, "Uploads"), "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    [A62SqlFact]
    public async Task Cv_objects_lost_on_Drive_or_removed_by_retention_are_uploaded_again()
    {
        await using var it = await A62Installation.CreateAsync();
        await it.Tick(); await it.Tick(BackupWorkerRoles.Restore);
        await Succeeds(it, "full");
        string lost, expired;
        await using (var db = it.Db())
        {
            var objects = await db.BackupObjects.OrderBy(o => o.ObjectKey).Take(2).ToListAsync();
            lost = objects[0].FileId; expired = objects[1].FileId;
            objects[1].DeletedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        it.Cloud.Files.TryRemove(lost, out _);
        it.Cloud.Files.TryRemove(expired, out _);
        var healed = await Succeeds(it, "full");
        Assert.Equal(2, healed.NewCvFiles);
        await Succeeds(it, "restore-drill", role: BackupWorkerRoles.Restore);
        await using (var db = it.Db())
            Assert.All(await db.BackupObjects.ToListAsync(), o => { Assert.Null(o.DeletedAtUtc); Assert.True(it.Cloud.Files.ContainsKey(o.FileId)); });
    }

    [A62SqlFact]
    public async Task A_partly_uploaded_transaction_log_is_published_by_the_next_run()
    {
        await using var it = await A62Installation.CreateAsync();
        await it.Tick();
        await Succeeds(it, "full");
        await it.Tick(); // scheduled first log and verify
        await it.Upload("captured by the log whose manifest upload fails");
        it.Cloud.FailUpload = name => name.Contains(".manifest.") ? BackupErrors.DriveUnavailable : null;
        Assert.Equal(BackupErrors.DriveUnavailable, (await Run(it, "log")).ErrorCode);
        it.Cloud.FailUpload = null;
        int before; await using (var db = it.Db()) before = await db.BackupPoints.CountAsync(p => p.Kind == "Log");
        await Succeeds(it, "log");
        await using (var db = it.Db()) Assert.Equal(before + 2, await db.BackupPoints.CountAsync(p => p.Kind == "Log"));
        Assert.Empty(Directory.GetFiles(Path.Combine(it.Staging, "logs-pending"), "*", SearchOption.AllDirectories));
    }

    [A62SqlFact]
    public async Task A_killed_worker_leaves_no_plaintext_and_loses_no_log()
    {
        await using var it = await A62Installation.CreateAsync();
        await it.Tick(); await it.Tick(BackupWorkerRoles.Restore);
        await Succeeds(it, "full");
        // Killed during a full: a plaintext database backup is left in staging.
        var leftover = Path.Combine(it.Staging, "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(leftover);
        await File.WriteAllTextAsync(Path.Combine(leftover, "database.bak"), "plaintext");
        // Killed during a restore: plaintext backup and key archive are left next to the database files.
        var restore = Path.Combine(it.Restores, "SmartCv_Restore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restore);
        foreach (var name in new[] { "database.bak", "log-0000.trn", "keys.zip", "data-0.mdf" }) await File.WriteAllTextAsync(Path.Combine(restore, name), "x");
        // Killed right after BACKUP LOG, before anything described the file: SQL Server will not give it again.
        var orphan = Path.Combine(it.Staging, "logs-pending", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        await it.Upload("only in the orphaned log");
        await it.Execute($"BACKUP LOG [{it.Catalog}] TO DISK = N'{Path.Combine(orphan, "log.trn")}' WITH CHECKSUM;");

        await it.Tick(BackupWorkerRoles.Restore);
        Assert.Equal("data-0.mdf", Assert.Single(Directory.GetFiles(restore).Select(file => Path.GetFileName(file))));
        await it.Upload("in the following log");
        await Succeeds(it, "log");
        Assert.False(Directory.Exists(leftover));
        Assert.False(Directory.Exists(orphan));
        // Both logs are in the catalog and the chain restores end to end.
        await using (var db = it.Db()) Assert.True(await db.BackupPoints.CountAsync(p => p.Kind == "Log") >= 2);
        Directory.Delete(restore, true);
        await Succeeds(it, "restore-drill", role: BackupWorkerRoles.Restore);
    }

    [A62SqlFact]
    public async Task Missing_altered_acknowledged_and_erased_cvs_are_each_treated_as_what_they_are()
    {
        await using var it = await A62Installation.CreateAsync();
        await it.Tick();
        var first = await Succeeds(it, "full");
        var victim = CvFiles(it)[0];
        var original = await File.ReadAllBytesAsync(victim);

        // Same size, different bytes, content already on Drive: still reported.
        var altered = (byte[])original.Clone(); altered[^1] ^= 1;
        await File.WriteAllBytesAsync(victim, altered);
        Assert.Equal(BackupErrors.Files, (await Run(it, "full")).ErrorCode);
        // The log still captures the new upload although an older file is broken.
        await it.Upload("uploaded while another file is damaged");
        var log = await Run(it, "log");
        Assert.Equal(BackupErrors.Files, log.ErrorCode);
        await using (var db = it.Db()) Assert.Equal(first.CvFiles, (await db.BackupPoints.SingleAsync(p => p.BackupPointId == log.BackupPointId)).CvFiles);

        // Missing: the worker says so before anybody presses a button.
        await File.WriteAllBytesAsync(victim, original);
        File.Move(victim, victim + ".away");
        await it.Tick();
        await using (var db = it.Db())
        {
            var heartbeat = await db.BackupWorkerHeartbeats.SingleAsync(h => h.Role == "backup");
            Assert.True(heartbeat.Ready); Assert.Equal(BackupErrors.Files, heartbeat.Problem);
            Assert.Contains((await new BackupAdminService(db, it.Clock).OverviewAsync(true, default)).Warnings, w => w.Code == "cv-files");
        }

        // Approved loss: skipped, counted on the point and audited. It does not hide any other file.
        Guid cvId = Guid.ParseExact(Path.GetFileNameWithoutExtension(victim), "D");
        var sha = Convert.ToHexStringLower(SHA256.HashData(original));
        void Acknowledge(BackupWorkerOptions o) => o.KnownMissingCvs.Add(new(cvId.ToString(), sha, KnownMissingRules.AcceptedLoss, "data owner", "2026-11-26"));
        var accepted = await Succeeds(it, "full", change: Acknowledge);
        await using (var db = it.Db())
        {
            Assert.Equal(1, (await db.BackupPoints.SingleAsync(p => p.BackupPointId == accepted.BackupPointId)).KnownMissingCvs);
            Assert.True(await db.AuditEvents.IgnoreQueryFilters().AnyAsync(a => a.ActionCode == "backup.known_missing_applied"));
        }
        // It comes back: backed up again although the acknowledgement is still configured.
        File.Move(victim + ".away", victim);
        var back = await Succeeds(it, "full", change: Acknowledge);
        await using (var db = it.Db()) Assert.Equal(0, (await db.BackupPoints.SingleAsync(p => p.BackupPointId == back.BackupPointId)).KnownMissingCvs);

        // Erased by privacy rules: no file expected, nothing to acknowledge.
        await it.Execute($"UPDATE dbo.Cvs SET StoredPath = N'(da-xoa)', AnonymizedAt = SYSUTCDATETIME() WHERE CvId = '{cvId}'", it.Catalog);
        File.Delete(victim);
        var erased = await Succeeds(it, "full");
        Assert.Equal(back.CvFiles - 1, erased.CvFiles);
    }

    [A62SqlFact]
    public async Task Nothing_pretends_to_work_before_the_first_backup_and_only_system_admins_get_in()
    {
        await using var it = await A62Installation.CreateAsync();
        await it.Tick(); await it.Tick(BackupWorkerRoles.Restore);
        await using (var db = it.Db())
        {
            var service = new BackupAdminService(db, it.Clock);
            foreach (var type in new[] { "log", "verify", "restore-drill" })
            {
                var (outcome, _, detail) = await service.RequestAsync(type, 1, true, default);
                Assert.Equal(BackupRequestOutcome.Blocked, outcome);
                Assert.Equal(BackupLabels.Error(BackupErrors.NoBackup), detail);
            }
        }
        await Succeeds(it, "full");
        await it.Tick();
        Assert.Contains(await it.Jobs(), j => j.Type == "verify" && j.Trigger == "scheduled" && j.Status == "Succeeded");
        await it.Tick();
        Assert.Single(await it.Jobs(), j => j.Type == "verify"); // weekly, not on every tick

        // A job refused by Google outranks an older "healthy" check.
        await using (var db = it.Db())
        {
            db.BackupJobs.Add(new() { BackupJobId = Guid.NewGuid(), Type = "catalog-sync", Trigger = "manual", Status = "Failed", ErrorCode = BackupErrors.DriveAuth, RequestedAtUtc = DateTime.UtcNow, FinishedAtUtc = DateTime.UtcNow.AddSeconds(1) });
            await db.SaveChangesAsync();
            var service = new BackupAdminService(db, it.Clock);
            var overview = await service.OverviewAsync(true, default);
            Assert.False(overview.Drive.LastCheckOk);
            Assert.Contains(overview.Blockers, b => b.Code == "drive-failed");
            Assert.Null(overview.NextFullAtUtc);
            Assert.Equal(BackupRequestOutcome.Blocked, (await service.RequestAsync("full", 1, true, default)).Outcome);
        }

        using var hr = it.World.Client(it.World.InternalHr, SmartCv.Tests.E2E.B57World.Internal);
        using var owner = it.World.Client(it.World.OwnerB, SmartCv.Tests.E2E.B57World.OwnerBWorkspace);
        foreach (var client in new[] { hr, owner })
        {
            foreach (var url in new[] { "overview", "points", "report", "report/export" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/backups/" + url)).StatusCode);
            foreach (var url in new[] { "jobs/full", "jobs/log", "jobs/verify", "jobs/restore-drill", "jobs/catalog-sync", "recovery", "drive/connect", "drive/check" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/backups/" + url, new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/admin/backups/drive")).StatusCode);
        }
        using var admin = it.World.AdminClient();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/backups/overview")).StatusCode);

        // The service itself reports a key ring it cannot use, before a full backup would trip over it.
        File.Delete(Path.Combine(it.Root, "web-keys", "key-synthetic.xml"));
        await it.Tick();
        await using var after = it.Db();
        var heartbeat = await after.BackupWorkerHeartbeats.SingleAsync(h => h.Role == "backup");
        Assert.Equal((false, BackupErrors.Keys), (heartbeat.Ready, heartbeat.Problem));
    }

    [A62SqlFact]
    public async Task Key_rotation_keeps_older_points_verifiable_and_restorable()
    {
        await using var it = await A62Installation.CreateAsync();
        await it.Tick(); await it.Tick(BackupWorkerRoles.Restore);
        var old = await Succeeds(it, "full");
        var oldKey = it.Key;
        it.Key = RandomNumberGenerator.GetBytes(32); it.RetiredKeys = [oldKey];
        var renewed = await Succeeds(it, "full");
        Assert.Equal(renewed.CvFiles, renewed.NewCvFiles); // new key, new objects
        await Succeeds(it, "verify");
        await Succeeds(it, "restore-drill", old.BackupPointId, BackupWorkerRoles.Restore);

        // Without the retired key the old point is reported, but never marked damaged (retention would drop it).
        it.RetiredKeys = [];
        Assert.Equal(BackupErrors.KeyMismatch, (await Run(it, "verify")).ErrorCode);
        await using var db = it.Db();
        var point = await db.BackupPoints.SingleAsync(p => p.BackupPointId == old.BackupPointId);
        Assert.Equal(("Available", BackupErrors.KeyMismatch), (point.Status, point.VerifyResult));
    }

    [A62SqlFact]
    public async Task Points_rebuilt_from_Drive_are_never_aged_out_by_the_new_installation()
    {
        await using var it = await A62Installation.CreateAsync();
        await it.Tick();
        var original = await Succeeds(it, "full");
        // As on a replacement machine: an empty catalog, filled from the manifests on Drive.
        await it.Execute("DELETE FROM dbo.BackupPointObjects; DELETE FROM dbo.BackupPoints; DELETE FROM dbo.BackupObjects; UPDATE dbo.BackupSettings SET Paused = 1, RetentionCount = 7;", it.Catalog);
        await Succeeds(it, "catalog-sync");
        for (var i = 0; i < 8; i++) await Succeeds(it, "full");
        await using var db = it.Db();
        Assert.Equal("Available", (await db.BackupPoints.SingleAsync(p => p.BackupPointId == original.BackupPointId)).Status);
        Assert.Equal(7, await db.BackupPoints.CountAsync(p => p.Status == "Available" && p.Kind == "Full" && p.BackupJobId != null));
    }

    /// <summary>
    /// Runs the whole pipeline as two SQL logins that hold exactly the generated grants, not as an administrator.
    /// Creates server objects in master, so it only ever runs against a LocalDB instance.
    /// </summary>
    [A62SqlFact]
    public async Task Least_privilege_accounts_can_back_up_and_restore_and_nothing_more()
    {
        var server = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A62SqlFact.Setting));
        if (!server.DataSource.Contains("(localdb)", StringComparison.OrdinalIgnoreCase)) return;
        await using var it = await A62Installation.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var backup = "a62_backup_" + suffix; var restore = "a62_restore_" + suffix;
        var password = "Pw!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)) + "a1";
        string As(string login) => new SqlConnectionStringBuilder(it.Connection) { IntegratedSecurity = false, UserID = login, Password = password, Pooling = false }.ConnectionString;
        Task Tick(string login, string role)
        {
            var options = it.Options(role);
            var connection = As(login);
            var context = new BackupEngineContext(options, connection, () => new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options),
                new StaticBackupKeyProvider(it.Key), it.Cloud, it.Clock, it.Log);
            return new BackupCoordinator(context).TickAsync(default);
        }
        async Task<SmartCv.Core.Entities.BackupWorkerHeartbeat> Heartbeat(string role)
        {
            await using var db = it.Db();
            return await db.BackupWorkerHeartbeats.AsNoTracking().SingleAsync(h => h.Role == role);
        }
        async Task Apply(string script)
        {
            await using var connection = new SqlConnection(it.Server.ConnectionString);
            await connection.OpenAsync();
            foreach (var batch in BackupGrantScript.Batches(script))
            {
                await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
                await command.ExecuteNonQueryAsync();
            }
        }
        try
        {
            // Applied twice: re-running the installer must not fail or leave the claim unsigned.
            for (var i = 0; i < 2; i++)
                await Apply(BackupGrantScript.Build(it.Catalog, backup, restore, restoreClaim: true)
                    .Replace("FROM WINDOWS", $"WITH PASSWORD = N'{password}', CHECK_POLICY = OFF"));
            await Tick(backup, "backup"); await Tick(restore, "restore");
            var ready = await Heartbeat("backup");
            Assert.True(ready.Ready, ready.Problem); Assert.Null(ready.Problem);
            var restoreReady = await Heartbeat("restore");
            Assert.True(restoreReady.Ready, restoreReady.Problem);

            async Task Check(string type, string login, string role, Guid? point = null)
            {
                var id = await it.Request(type, point);
                await Tick(login, role);
                var job = await it.Job(id);
                Assert.True(job.Status == "Succeeded", $"{type}: {job.ErrorCode} at {job.Stage}\n{string.Join('\n', it.Log.Lines.TakeLast(6))}");
            }
            await Check("full", backup, "backup");
            await it.Upload("captured by the least-privilege log");
            await Check("log", backup, "backup");
            await Check("verify", backup, "backup");
            await Check("catalog-sync", backup, "backup");
            await Check("restore-drill", restore, "restore");
            Guid anchor; await using (var db = it.Db()) anchor = (await db.BackupPoints.SingleAsync(p => p.Kind == "Full")).BackupPointId;
            await Check("recovery", restore, "restore", anchor);

            // Neither account can read business data, and the backup account cannot create or claim anything.
            async Task Refused(string login, string sql)
            {
                await using var connection = new SqlConnection(As(login));
                await connection.OpenAsync();
                await using var command = new SqlCommand(sql, connection);
                await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
            }
            await Refused(backup, "SELECT TOP 1 Email FROM dbo.Users");
            await Refused(backup, "SELECT TOP 1 OriginalFileName FROM dbo.Cvs");
            await Refused(backup, "CREATE DATABASE a62_must_not_exist");
            await Refused(restore, "SELECT TOP 1 Email FROM dbo.Users");
            await Refused(restore, $"EXEC master.dbo.{BackupGrantScript.ClaimProcedure} @name = N'{it.Catalog}'");
            await Refused(restore, $"EXEC master.dbo.{BackupGrantScript.ClaimProcedure} @name = N'SmartCv_Restore_{new string('a', 32)}'");

            // Missing permissions are reported by the worker itself.
            await it.Execute($"REVOKE EXECUTE ON dbo.{BackupGrantScript.ClaimProcedure} FROM [{restore}];", "master");
            await Tick(restore, "restore");
            Assert.Equal((false, BackupErrors.Privilege), ((await Heartbeat("restore")).Ready, (await Heartbeat("restore")).Problem));
            await it.Execute($"REVOKE SELECT ON dbo.Cvs (FileHash) FROM [{backup}];", it.Catalog);
            await Tick(backup, "backup");
            Assert.Equal((false, BackupErrors.Privilege), ((await Heartbeat("backup")).Ready, (await Heartbeat("backup")).Problem));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await it.Execute($"IF OBJECT_ID(N'dbo.{BackupGrantScript.ClaimProcedure}') IS NOT NULL DROP PROCEDURE dbo.{BackupGrantScript.ClaimProcedure};" +
                " IF SUSER_ID(N'SCAppRestoreClaimSigner') IS NOT NULL DROP LOGIN SCAppRestoreClaimSigner; IF CERT_ID(N'SCAppRestoreClaimCert') IS NOT NULL DROP CERTIFICATE SCAppRestoreClaimCert;", "master");
            foreach (var login in new[] { backup, restore })
            {
                await it.Execute($"IF USER_ID(N'{login}') IS NOT NULL DROP USER [{login}];", "msdb");
                await it.Execute($"IF USER_ID(N'{login}') IS NOT NULL DROP USER [{login}];", "master");
                // A restored database this login still owns (a kept recovery) must be dropped before the login.
                await it.Execute($"DECLARE @s nvarchar(max)=N''; SELECT @s+=N'ALTER AUTHORIZATION ON DATABASE::'+QUOTENAME(name)+N' TO sa;' FROM sys.databases WHERE owner_sid=SUSER_SID(N'{login}'); EXEC(@s);", "master");
                await it.Execute($"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}];", "master");
            }
        }
    }
}
