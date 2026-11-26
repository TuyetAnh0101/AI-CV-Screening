using System.Text.Json;
using SmartCv.Core.Backup;

namespace SmartCv.Infrastructure.Backup;

public sealed record ManifestFile(string FileId, string Name, long PlainBytes, string PlainSha256, long CipherBytes, string CipherSha256, string CipherMd5);
public sealed record ManifestCv(string Path, long Bytes, string Sha256, string ObjectKey, string FileId, long CipherBytes, string CipherSha256);
public sealed record ManifestKnownMissing(string CvId, string Sha256, string Reason);
public sealed record ManifestSql(string? FirstLsn, string? LastLsn, DateTime? FinishedServerTime, string Edition, string Version, string RecoveryModel, bool CopyOnly);

/// <summary>
/// Self-describing, encrypted restore point. Uploaded last: a point exists on Drive only when every file it lists
/// was uploaded and verified. Recovery after losing this machine needs only Drive, the key and this manifest.
/// </summary>
public sealed record BackupManifest(int SchemaVersion, string ManagedBy, Guid PointId, string Kind, string Database, DateTime CreatedAtUtc,
    string KeyId, string Consistency, string? AppSchemaVersion, string CvStorageRoot, ManifestSql Sql, ManifestFile Payload, ManifestFile? Keys,
    IReadOnlyList<ManifestCv> CvFiles, IReadOnlyList<ManifestKnownMissing> KnownMissing, int OrphanFiles, string ToolVersion)
{
    public const string Producer = "SCApp.A62.GoogleDrive";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string FileName(string kind, DateTime createdUtc, Guid point, string part) =>
        $"scapp-{kind.ToLowerInvariant()}-{createdUtc:yyyyMMdd'T'HHmmss'Z'}-{point:N}.{part}.enc";
    public static string ObjectName(string objectKey) => $"scapp-cv-{objectKey}.enc";

    /// <summary>Decrypts with whichever of the machine's keys the file was written with.</summary>
    public static async Task<BackupManifest> DownloadAsync(IBackupCloudStore store, IBackupKeyProvider keys, string fileId, CancellationToken ct)
    {
        using var cipher = new MemoryStream();
        await store.DownloadAsync(fileId, cipher, ct);
        using var key = keys.Load(await BackupCrypto.ReadKeyIdAsync(cipher, ct));
        using var plain = new MemoryStream();
        await BackupCrypto.DecryptAsync(key, BackupCrypto.Readable(cipher), plain, ct);
        BackupManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<BackupManifest>(plain.ToArray(), Json); }
        catch (JsonException) { throw new BackupException(BackupErrors.Checksum); }
        if (manifest is null || manifest.SchemaVersion != 1 || manifest.ManagedBy != Producer || manifest.KeyId != key.KeyId)
            throw new BackupException(BackupErrors.Checksum);
        return manifest;
    }
}
