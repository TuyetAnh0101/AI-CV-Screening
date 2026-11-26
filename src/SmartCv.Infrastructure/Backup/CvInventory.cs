using System.Security.Cryptography;
using SmartCv.Core.Backup;

namespace SmartCv.Infrastructure.Backup;

public enum CvFileState
{
    /// <summary>File present with the recorded size (and hash, when hashes were checked).</summary>
    Ok,
    Missing,
    SizeMismatch,
    HashMismatch,
    /// <summary>StoredPath is not a plain relative path below the CV storage root.</summary>
    UnsafePath,
    /// <summary>Missing, and recorded one by one in the worker settings with a reason and an approval.</summary>
    AcknowledgedMissing,
    Unreadable
}

public sealed record CvFileCheck(CvRow Row, string Relative, CvFileState State)
{
    public bool IsProblem => State is not (CvFileState.Ok or CvFileState.AcknowledgedMissing);
}

/// <summary>
/// The one rule for "where is this CV and is it intact", shared by the backup, the readiness check and the
/// operator report. Same containment rule as LocalCvFileStorage: below {base}/{storage root}, nothing else.
/// </summary>
public static class CvInventory
{
    public static string Root(string baseDirectory, string storageRoot) =>
        Path.GetFullPath(Path.Combine(baseDirectory, storageRoot.Replace('\\', '/').Trim('/').Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The path below the storage root, or null when the stored value could point anywhere else.</summary>
    public static string? Relative(string storedPath, string storageRoot)
    {
        var prefix = storageRoot.Replace('\\', '/').Trim('/') + "/";
        var stored = storedPath.Replace('\\', '/');
        if (!stored.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = stored[prefix.Length..];
        return relative.Length == 0 || relative.Contains(':') || relative.Split('/').Any(p => p is "" or "." or "..") ? null : relative;
    }

    public static async Task<List<CvFileCheck>> CheckAsync(IEnumerable<CvRow> rows, string baseDirectory, string storageRoot,
        IEnumerable<KnownMissingCv> knownMissing, bool verifyHash, CancellationToken ct)
    {
        var acknowledged = KnownMissingRules.Validate(knownMissing);
        var root = Root(baseDirectory, storageRoot);
        var result = new List<CvFileCheck>();
        foreach (var row in rows)
        {
            var relative = Relative(row.StoredPath, storageRoot);
            var full = relative is null ? null : Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (full is null || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new(row, "", CvFileState.UnsafePath));
                continue;
            }
            result.Add(new(row, relative!, await StateAsync(row, full, acknowledged, verifyHash, ct)));
        }
        return result;
    }

    private static async Task<CvFileState> StateAsync(CvRow row, string full, Dictionary<Guid, string> acknowledged, bool verifyHash, CancellationToken ct)
    {
        try
        {
            var file = new FileInfo(full);
            if (!file.Exists)
            {
                // "Exists" is also false when the account is not allowed to look: that is not a lost file.
                if (Denied(full)) return CvFileState.Unreadable;
                // A file that reappears is checked and backed up again: the acknowledgement only covers absence.
                return acknowledged.TryGetValue(row.CvId, out var sha) && sha == row.Sha256 ? CvFileState.AcknowledgedMissing : CvFileState.Missing;
            }
            if (file.Length != row.Bytes) return CvFileState.SizeMismatch;
            // Opened even without hashing: a file the account cannot read is not a file it can back up.
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!verifyHash) return CvFileState.Ok;
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) == row.Sha256 ? CvFileState.Ok : CvFileState.HashMismatch;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return CvFileState.Unreadable; }
    }

    private static bool Denied(string full)
    {
        try { File.GetAttributes(full); return false; }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return false; } // includes file and folder not found
    }
}
