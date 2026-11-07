using System.Security.Cryptography;

namespace SmartCv.Core.Uploads;

/// <summary>Tính mã SHA-256 của file: 64 ký tự hex viết HOA, khớp cột Cvs.FileHash CHAR(64) và dữ liệu seed.</summary>
public static class FileHasher
{
    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken = default)
    {
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);   // viết hoa, ví dụ "221BD3561C96..."
    }
}
