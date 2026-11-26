using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartCv.Core.Backup;

namespace SmartCv.Infrastructure.Backup;

public sealed record BackupKeyInfo(string KeyId, DateTime CreatedAtUtc, DateTime? EscrowConfirmedAtUtc);

public interface IBackupKeyProvider
{
    /// <summary>The current key, or a <see cref="BackupException"/> with <see cref="BackupErrors.Key"/>.</summary>
    BackupKey Load();
    BackupKeyInfo? Info();

    /// <summary>The current or a retired key with this id: older restore points stay readable after a rotation.</summary>
    BackupKey Load(string keyId)
    {
        var current = Load();
        if (current.KeyId == keyId) return current;
        current.Dispose();
        throw new BackupException(BackupErrors.KeyMismatch);
    }
}

/// <summary>Fixed keys for tests and restore drills of synthetic data. The first one is current.</summary>
public sealed class StaticBackupKeyProvider(byte[] key, params byte[][] retired) : IBackupKeyProvider
{
    public BackupKey Load() => new(key);
    public BackupKeyInfo? Info() { using var k = Load(); return new(k.KeyId, DateTime.UtcNow, DateTime.UtcNow); }

    public BackupKey Load(string keyId)
    {
        foreach (var candidate in retired.Prepend(key))
        {
            var loaded = new BackupKey(candidate);
            if (loaded.KeyId == keyId) return loaded;
            loaded.Dispose();
        }
        throw new BackupException(BackupErrors.KeyMismatch);
    }
}

/// <summary>
/// backup-key.dat: the current key protected with DPAPI machine scope; file ACL limits it to the worker accounts
/// and Administrators. backup-key.json: id and whether the operator confirmed an offline copy. Rotated keys stay as
/// backup-key.{id}.dat. Losing both the machine and the offline copies makes those backups unreadable, by design.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiBackupKeyProvider(string directory) : IBackupKeyProvider
{
    public const string KeyFile = "backup-key.dat";
    public const string InfoFile = "backup-key.json";
    private static readonly byte[] Entropy = "SCApp.Backup.Key.v1"u8.ToArray();
    private static readonly Regex KeyIdShape = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);

    private static string RetiredFile(string keyId) => $"backup-key.{keyId}.dat";

    public BackupKey Load() => Read(KeyFile);

    public BackupKey Load(string keyId)
    {
        if (!KeyIdShape.IsMatch(keyId)) throw new BackupException(BackupErrors.KeyMismatch);
        var current = Load();
        if (current.KeyId == keyId) return current;
        current.Dispose();
        if (!File.Exists(Path.Combine(directory, RetiredFile(keyId)))) throw new BackupException(BackupErrors.KeyMismatch);
        var retired = Read(RetiredFile(keyId));
        if (retired.KeyId == keyId) return retired;
        retired.Dispose();
        throw new BackupException(BackupErrors.KeyMismatch);
    }

    private BackupKey Read(string name)
    {
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(directory, name)), Entropy, DataProtectionScope.LocalMachine);
            try { return new BackupKey(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException) { throw new BackupException(BackupErrors.Key); }
    }

    public BackupKeyInfo? Info()
    {
        try { return JsonSerializer.Deserialize<BackupKeyInfo>(File.ReadAllText(Path.Combine(directory, InfoFile))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Writes a new key; refuses to replace an existing one (that would orphan every backup).</summary>
    public string Create(ReadOnlySpan<byte> key, bool escrowConfirmed)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, KeyFile);
        if (File.Exists(path)) throw new BackupException(BackupErrors.Config);
        using var created = new BackupKey(key);
        Write(path, key);
        WriteInfo(created.KeyId, escrowConfirmed);
        return created.KeyId;
    }

    /// <summary>Makes <paramref name="key"/> current and keeps the old one for reading. Nothing is deleted.</summary>
    public string Rotate(ReadOnlySpan<byte> key, bool escrowConfirmed)
    {
        using var old = Load();
        using var created = new BackupKey(key);
        if (created.KeyId == old.KeyId) throw new BackupException(BackupErrors.Config);
        var current = Path.Combine(directory, KeyFile);
        var retired = Path.Combine(directory, RetiredFile(old.KeyId));
        if (!File.Exists(retired)) File.Copy(current, retired, overwrite: false);
        var staged = current + ".new";
        if (File.Exists(staged)) File.Delete(staged);
        Write(staged, key);
        // Copy over the existing file so its access list (worker accounts) is preserved.
        File.Copy(staged, current, overwrite: true);
        File.Delete(staged);
        WriteInfo(created.KeyId, escrowConfirmed);
        return created.KeyId;
    }

    /// <summary>Adds an older key (typed from its offline copy) so points made with it can be restored.</summary>
    public string ImportRetired(ReadOnlySpan<byte> key)
    {
        using var current = Load();
        using var added = new BackupKey(key);
        if (added.KeyId == current.KeyId) return added.KeyId;
        var path = Path.Combine(directory, RetiredFile(added.KeyId));
        if (!File.Exists(path)) Write(path, key);
        return added.KeyId;
    }

    /// <summary>Ids of the retired keys present on this machine.</summary>
    public IReadOnlyList<string> RetiredKeyIds() => Directory.Exists(directory)
        ? Directory.GetFiles(directory, "backup-key.*.dat").Select(f => Path.GetFileName(f)!.Split('.')[1]).Where(id => KeyIdShape.IsMatch(id)).ToList()
        : [];

    private static void Write(string path, ReadOnlySpan<byte> key)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(ProtectedData.Protect(key.ToArray(), Entropy, DataProtectionScope.LocalMachine));
        file.Flush(flushToDisk: true);
    }

    private void WriteInfo(string keyId, bool escrowConfirmed) => File.WriteAllText(Path.Combine(directory, InfoFile),
        JsonSerializer.Serialize(new BackupKeyInfo(keyId, DateTime.UtcNow, escrowConfirmed ? DateTime.UtcNow : null)));
}
