using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Backup;
using SmartCv.Infrastructure.Backup;

namespace SmartCv.BackupWorker;

public static class WorkerCommands
{
    /// <summary>
    /// Read-only readiness check. Prints results and codes, never secrets or key material. SQL permissions are NOT
    /// judged from the account running this command (usually an administrator): the running service checks its own
    /// account and publishes the answer, and that answer is what is reported here.
    /// </summary>
    public static async Task<int> CheckAsync(IServiceProvider services, CancellationToken ct)
    {
        var context = services.GetRequiredService<BackupEngineContext>();
        var options = context.Options;
        var failed = 0;
        void Report(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {name}{(detail.Length > 0 ? " - " + detail : "")}");
            if (!ok) failed++;
        }
        void Note(string name, string detail) => Console.WriteLine($"[INFO] {name} - {detail}");
        var invalid = options.Validate();
        Report("configuration", invalid is null, invalid ?? $"role {options.Role}");
        try { using var key = context.Keys.Load(); Report("backup key", true, "id …" + key.KeyId[^6..]); }
        catch (BackupException error) { Report("backup key", false, error.Code + ": run init-key"); }
        Report("key escrow confirmed", context.Keys.Info()?.EscrowConfirmedAtUtc is not null, "an offline copy of the key must exist (test it with verify-key)");
        try
        {
            Directory.CreateDirectory(options.StagingDirectory);
            var probe = Path.Combine(options.StagingDirectory, ".probe-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(probe, "x", ct); File.Delete(probe);
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.StagingDirectory))!).AvailableFreeSpace;
            Report("staging writable and space", free > options.MinimumFreeSpaceMb * 1024 * 1024, $"{free / 1073741824.0:0.0} GB free");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { Report("staging writable and space", false, error.GetType().Name); }
        if (options.Role == BackupWorkerRoles.Backup)
        {
            Report("CV storage readable", Directory.Exists(context.CvRoot));
            foreach (var (name, path) in new[] { ("API key ring", options.ApiKeysPath), ("Web key ring", options.WebKeysPath) })
            {
                int count;
                try { count = Directory.GetFiles(path, "key-*.xml").Length; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { count = -1; }
                Report(name, count > 0, count >= 0 ? $"{count} key file(s)" : "not readable");
            }
        }
        try
        {
            await using var sql = await BackupSql.OpenAsync(context.ConnectionString, 30, ct);
            var info = await sql.DatabaseInfoAsync(ct);
            var login = await sql.ScalarAsync("SELECT SUSER_SNAME();", ct) as string;
            Report("SQL connection", true, $"{sql.Database}, {info.RecoveryModel}, this command runs as {login}");
            if (options.Role == BackupWorkerRoles.Backup && invalid is null)
            {
                var checks = await CvInventory.CheckAsync(await sql.CvRowsAsync(ct), options.CvBaseDirectory, options.CvStorageRoot, options.KnownMissingCvs, verifyHash: true, ct);
                var problems = checks.Count(x => x.IsProblem);
                var acknowledged = checks.Count(x => x.State == CvFileState.AcknowledgedMissing);
                Report("referenced CV files intact", problems == 0,
                    problems == 0 ? $"{checks.Count} rows, {acknowledged} acknowledged missing" : $"{problems} of {checks.Count} missing or altered: run cv-report");
            }
        }
        catch (BackupException error) { Report("SQL", false, error.Code); }
        catch (Microsoft.Data.SqlClient.SqlException error) { Report("SQL", false, "SQL error " + error.Number); }
        try
        {
            await using var db = context.Db();
            var heartbeat = await db.BackupWorkerHeartbeats.AsNoTracking().SingleOrDefaultAsync(h => h.Role == options.Role, ct);
            if (heartbeat is null) Report("service self-check (account permissions)", false, "the service has never reported: start it");
            else
            {
                var age = DateTime.UtcNow - heartbeat.HeartbeatAtUtc;
                var detail = $"reported {age.TotalSeconds:0}s ago by the running service: {(heartbeat.Ready ? "ready" : "not ready")}{(heartbeat.Problem is null ? "" : ", " + heartbeat.Problem)}";
                Report("service self-check (account permissions)", age < BackupAdminService.WorkerStale && heartbeat.Ready
                    && heartbeat.Problem is not (BackupErrors.Privilege or BackupErrors.ExcessPrivilege), detail);
            }
            var connection = await db.BackupDriveConnections.SingleOrDefaultAsync(ct);
            if (connection is null) Report("Google Drive", false, "not connected: use /admin/backups");
            else
            {
                var store = await context.Cloud.OpenAsync(db, ct);
                Report("Google Drive", await store.GetAsync(connection.FolderId, ct) is not null, "folder reachable");
            }
        }
        catch (BackupException error) { Report("Google Drive", false, error.Code); }
        catch (Microsoft.Data.SqlClient.SqlException error) { Report("Google Drive", false, "catalog not readable (SQL " + error.Number + "); apply the migration"); }
        Note("scope", "this checks readiness only: it does not take, verify or restore any backup");
        Console.WriteLine(failed == 0 ? "READY" : $"NOT READY: {failed} check(s) failed.");
        return failed == 0 ? 0 : 2;
    }

    /// <summary>
    /// Which referenced CVs are missing or altered, by id and hash only (no names, no content). Nothing is changed
    /// and nothing is acknowledged: acknowledging stays a deliberate, per-record decision in the settings file.
    /// </summary>
    public static async Task<int> CvReportAsync(IServiceProvider services, CancellationToken ct)
    {
        var context = services.GetRequiredService<BackupEngineContext>();
        var options = context.Options;
        await using var sql = await BackupSql.OpenAsync(context.ConnectionString, 60, ct);
        var checks = await CvInventory.CheckAsync(await sql.CvRowsAsync(ct), options.CvBaseDirectory, options.CvStorageRoot, options.KnownMissingCvs, verifyHash: true, ct);
        Console.WriteLine($"CV storage: {context.CvRoot}");
        foreach (var group in checks.GroupBy(x => x.State).OrderBy(g => g.Key)) Console.WriteLine($"  {group.Key}: {group.Count()}");
        foreach (var check in checks.Where(x => x.State != CvFileState.Ok).OrderBy(x => x.State).ThenBy(x => x.Row.CvId))
        {
            var demo = KnownMissingRules.DemoRows.TryGetValue(check.Row.CvId.ToString(), out var seeded) && seeded == check.Row.Sha256;
            Console.WriteLine($"{check.State,-19} CvId={check.Row.CvId} Sha256={check.Row.Sha256}{(demo ? "  [row of database/seed_demo.sql]" : "")}");
        }
        var problems = checks.Count(x => x.IsProblem);
        Console.WriteLine(problems == 0 ? "No unresolved CV file." : $"{problems} unresolved: restore the files to CV storage (see docs/van-hanh.md).");
        return problems == 0 ? 0 : 2;
    }

    /// <summary>Creates, imports or rotates the key. A new key is shown once so the operator can store it offline.</summary>
    public static int InitKey(BackupWorkerOptions options, bool import, bool rotate = false, bool retired = false)
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Windows only."); return 1; }
        if (Console.IsInputRedirected || Console.IsOutputRedirected) { Console.WriteLine("Run init-key in an interactive console (the key must not go to a file or a log)."); return 1; }
        if (rotate && (import || retired) || retired && !import) { Console.WriteLine("Use: init-key | init-key --import | init-key --import --retired | init-key --rotate"); return 1; }
        var provider = new DpapiBackupKeyProvider(options.KeyDirectory);
        var existing = provider.Info();
        if (existing is not null && !rotate && !retired) { Console.WriteLine($"A key already exists (id …{existing.KeyId[^6..]}). Refusing to replace it. Use --rotate to change keys and keep the old one."); return 1; }
        if (existing is null && (rotate || retired)) { Console.WriteLine("There is no current key yet."); return 1; }
        byte[] key;
        if (import)
        {
            Console.WriteLine("Paste the saved recovery key (input is hidden), then Enter:");
            try { key = BackupKey.Parse(ReadHidden()); }
            catch (BackupException) { Console.WriteLine("That is not a valid backup key."); return 1; }
        }
        else
        {
            if (rotate) Console.WriteLine($"Rotating: key …{existing!.KeyId[^6..]} stays on this machine for older backups. KEEP its offline copy too.");
            key = RandomNumberGenerator.GetBytes(BackupKey.Length);
            Console.WriteLine();
            Console.WriteLine("RECOVERY KEY - shown once. Store it in a password manager or printed, NOT on this computer and NOT in the backup's Google Drive.");
            Console.WriteLine("Do not copy it into a chat, e-mail, ticket or screenshot. Without it, backups cannot be decrypted if this computer is lost.");
            Console.WriteLine();
            Console.WriteLine("    " + BackupKey.Format(key));
            Console.WriteLine();
        }
        Console.Write(import ? "Type YES to store the imported key: " : "After saving it, type SAVED: ");
        var confirmed = Console.ReadLine()?.Trim() == (import ? "YES" : "SAVED");
        // Remove the key from the screen and, where the terminal allows it, from its scrollback.
        if (!import) { Console.Write("\u001b[3J"); Console.Clear(); }
        if (!confirmed) { Console.WriteLine("Nothing was written."); return 1; }
        var id = retired ? provider.ImportRetired(key) : rotate ? provider.Rotate(key, escrowConfirmed: true) : provider.Create(key, escrowConfirmed: true);
        CryptographicOperations.ZeroMemory(key);
        Console.WriteLine(retired ? $"Older key available for restores (id …{id[^6..]})." : $"Key stored for this machine (id …{id[^6..]}).");
        if (!import) Console.WriteLine("Close this console window, then test your saved copy with: verify-key. Restart the worker services.");
        return 0;
    }

    /// <summary>Proves an offline copy is usable: says which key of this machine it is, without storing anything.</summary>
    public static int VerifyKey(BackupWorkerOptions options)
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Windows only."); return 1; }
        if (Console.IsInputRedirected) { Console.WriteLine("Run verify-key in an interactive console."); return 1; }
        var provider = new DpapiBackupKeyProvider(options.KeyDirectory);
        Console.WriteLine("Type or paste the recovery key from your offline copy (input is hidden), then Enter:");
        string id;
        try { using var typed = new BackupKey(BackupKey.Parse(ReadHidden())); id = typed.KeyId; }
        catch (BackupException) { Console.WriteLine("That is not a valid backup key."); return 1; }
        var current = provider.Info()?.KeyId;
        if (id == current) { Console.WriteLine($"MATCH: this is the CURRENT key (id …{id[^6..]})."); return 0; }
        if (provider.RetiredKeyIds().Contains(id)) { Console.WriteLine($"This is a RETIRED key (id …{id[^6..]}): older backups only, new ones use …{current?[^6..]}."); return 3; }
        Console.WriteLine($"NO MATCH: key …{id[^6..]} is not on this machine; new backups use …{current?[^6..] ?? "(none)"}.");
        return 3;
    }

    private static string ReadHidden()
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return text.ToString();
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
    }

    /// <summary>Least-privilege grants for the two service accounts. Printed for review; nothing is executed.</summary>
    public static int PrintSqlGrants(string database, string backupAccount, string restoreAccount, bool restoreClaim)
    {
        Console.Write(BackupGrantScript.Build(database, backupAccount, restoreAccount, restoreClaim));
        return 0;
    }
}
