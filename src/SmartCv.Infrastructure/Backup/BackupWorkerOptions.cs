using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SmartCv.Core.Backup;

namespace SmartCv.Infrastructure.Backup;

/// <summary>Configuration of the separate backup/restore worker process. Contains no secrets.</summary>
public sealed class BackupWorkerOptions
{
    public const string SectionName = "BackupWorker";
    public string Role { get; set; } = BackupWorkerRoles.Backup;
    public string CvBaseDirectory { get; set; } = "";
    public string CvStorageRoot { get; set; } = "Uploads/CV";
    public string ApiKeysPath { get; set; } = "";
    public string WebKeysPath { get; set; } = "";
    /// <summary>Temporary only: plaintext exists here between SQL BACKUP and encryption.</summary>
    public string StagingDirectory { get; set; } = "";
    public long StagingLimitMb { get; set; } = 4096;
    public long MinimumFreeSpaceMb { get; set; } = 2048;
    public string KeyDirectory { get; set; } = "";
    public string RestoreFilesRoot { get; set; } = "";
    public int PrivacyLockSeconds { get; set; } = 60;
    public int CommandTimeoutSeconds { get; set; } = 3600;
    public int PollSeconds { get; set; } = 15;
    public int JobTimeoutMinutes { get; set; } = 240;
    /// <summary>A drill removes the database it created after a successful verification.</summary>
    public bool KeepDrillDatabase { get; set; }
    public List<KnownMissingCv> KnownMissingCvs { get; set; } = [];

    public string? Validate()
    {
        if (Role is not (BackupWorkerRoles.Backup or BackupWorkerRoles.Restore)) return "Role";
        foreach (var (name, value) in new[] { ("CvBaseDirectory", CvBaseDirectory), ("StagingDirectory", StagingDirectory), ("KeyDirectory", KeyDirectory) })
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return name;
        if (Role == BackupWorkerRoles.Backup)
            foreach (var (name, value) in new[] { ("ApiKeysPath", ApiKeysPath), ("WebKeysPath", WebKeysPath) })
                if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return name;
        if (Role == BackupWorkerRoles.Restore && (string.IsNullOrWhiteSpace(RestoreFilesRoot) || !Path.IsPathFullyQualified(RestoreFilesRoot))) return "RestoreFilesRoot";
        var root = CvStorageRoot.Replace('\\', '/').Trim('/');
        if (root.Length == 0 || root.Split('/').Any(p => p is "" or "." or "..") || Path.IsPathRooted(CvStorageRoot)) return "CvStorageRoot";
        if (PrivacyLockSeconds is < 1 or > 600 || PollSeconds is < 2 or > 300 || CommandTimeoutSeconds < 60 || StagingLimitMb < 64) return "Limits";
        try { KnownMissingRules.Validate(KnownMissingCvs); } catch (BackupException) { return "KnownMissingCvs"; }
        return null;
    }
}

public sealed record KnownMissingCv(string CvId, string Sha256, string Reason, string? ApprovedBy = null, string? ApprovedOn = null);

/// <summary>
/// A referenced CV without its file normally fails the backup. Exactly two exceptions, one record at a time:
/// the seeded demo rows that never had files, and a loss the data owner accepted. Both need the name of the
/// person who approved it and the date; the worker writes an audit event whenever a backup relies on them.
/// </summary>
public static class KnownMissingRules
{
    public const string DemoSeed = "demo-seed-never-had-file";
    public const string AcceptedLoss = "loss-accepted-by-data-owner";

    /// <summary>The 12 rows of database/seed_demo.sql (id and FileHash): the only demo exception.</summary>
    public static IReadOnlyDictionary<string, string> DemoRows { get; } = new Dictionary<string, string>
    {
        ["c7ba1810-54f6-5d77-a22f-daf0023a079d"] = "221bd3561c9671cfd1b6e91db2a6f78220c21f2df158a195fa50dc6b284bd290",
        ["0f2bf90d-9d52-5d3b-b5c9-64efcbcd2ed7"] = "9db39a284fc09efa0f649227b24451343b23524fa4a26915989a54ef5f214540",
        ["8779bbf9-1e4d-5321-9337-c42a324773f2"] = "5bcfabb01f26e708eaab6ad2f8b4e5233fd34a9cc4b58acb2b56af8cfdbc70f5",
        ["e7421830-8b15-5151-bbf1-f9d98274ad99"] = "71c0227707fb26615fd237c26be837dd7ca541bf1f0d1ccd01927d5161e75885",
        ["b4dbe125-2e0c-5d04-b603-711d91b54940"] = "4787584997a0226f317be60b49337661f164284090d38bbba74fd17dd777733b",
        ["9fc710e9-ec14-51d3-9abe-9347d627a147"] = "2f5b35d5040ed01074efc7c9575efefa479afcb9f14968eb5802caf03cbb4258",
        ["a541bc02-099e-5df6-98b4-d72d4c8f14e2"] = "b7f23e7f0558ce62fe564942d3ec7104e54d468e715fbd3463d1823d376a8b39",
        ["6601e62f-7977-535e-99e4-857437192366"] = "c589e2b9901e04e91294a2d41487aa4d2159ad5d8ee5bbb3f69078c3d32bd4fa",
        ["c0e668f3-ab0a-5c9e-932f-d619895fe6bd"] = "0f06fefeae3531b7e165e99b72dbfa78735bfaab480ca4d7e977d7a5c65dea65",
        ["13a513e4-1972-5c93-8c13-6f624770f864"] = "d8c3aab93baa3bf18e13775cdb6614df09df4244f790f5402ca84e21068baa29",
        ["f65dcda8-3a1b-50c5-a010-726c29fa1030"] = "3b329142456e21f555bd6ed7dca90567b2e326c78eb6524400787a1f6d29d3bc",
        ["e461f5ab-0efb-598a-9c87-5c203f175ec9"] = "04ab019529c5ba5a836a8d8a0939009eef51f209f9770e2f3101756425f4269f"
    };

    public static Dictionary<Guid, string> Validate(IEnumerable<KnownMissingCv> entries)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var entry in entries)
        {
            if (!Guid.TryParse(entry.CvId, out var id) || entry.Sha256 is not { Length: 64 } || result.ContainsKey(id)) throw new BackupException(BackupErrors.Config);
            var sha = entry.Sha256.ToLowerInvariant();
            var approved = !string.IsNullOrWhiteSpace(entry.ApprovedBy) && DateOnly.TryParseExact(entry.ApprovedOn, "yyyy-MM-dd", out _);
            var ok = approved && entry.Reason switch
            {
                DemoSeed => DemoRows.TryGetValue(id.ToString(), out var seeded) && seeded == sha,
                AcceptedLoss => true,
                _ => false
            };
            if (!ok) throw new BackupException(BackupErrors.Config);
            result[id] = sha;
        }
        return result;
    }
}

/// <summary>Google credentials at rest: Data Protection ciphertext readable by the API and the worker only.</summary>
public sealed class BackupDriveCredentials(IDataProtectionProvider provider)
{
    private const string Purpose = "SmartCv.Backup.GoogleDrive.Credential.v1";
    public string Protect(GoogleCredential credential) => provider.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(credential));
    public GoogleCredential Unprotect(string text)
    {
        try { return JsonSerializer.Deserialize<GoogleCredential>(provider.CreateProtector(Purpose).Unprotect(text)) ?? throw new BackupException(BackupErrors.DriveAuth); }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        { throw new BackupException(BackupErrors.DriveAuth); }
    }
}

public interface IBackupCloudStoreFactory
{
    /// <summary>The configured Drive folder, or <see cref="BackupErrors.DriveNotConnected"/>.</summary>
    Task<IBackupCloudStore> OpenAsync(Data.SmartCvDbContext db, CancellationToken ct);
}

public sealed class GoogleDriveStoreFactory(BackupDriveCredentials credentials, IHttpClientFactory http) : IBackupCloudStoreFactory
{
    public const string HttpClientName = "SCApp.GoogleDrive";
    public async Task<IBackupCloudStore> OpenAsync(Data.SmartCvDbContext db, CancellationToken ct)
    {
        var connection = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleOrDefaultAsync(db.BackupDriveConnections, ct)
            ?? throw new BackupException(BackupErrors.DriveNotConnected);
        return new GoogleDriveStore(http.CreateClient(HttpClientName), credentials.Unprotect(connection.ProtectedCredential), connection.FolderId);
    }
}
