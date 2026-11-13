using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Storage;

namespace SmartCv.Infrastructure.Storage;

/// <summary>
/// Lưu file CV vào thư mục trên máy chủ: {contentRoot}/Uploads/CV/yyyy/MM/{GUID}{đuôi}.
/// Tên file là GUID (không dùng tên gốc) để: không trùng tên, không lộ tên ứng viên trên ổ đĩa,
/// và không bị lỗi với tên chứa ký tự lạ hoặc đường dẫn giả mạo kiểu "../../".
/// Thư mục nằm ngoài wwwroot nên không truy cập trực tiếp được qua web; chỉ đọc qua GET /api/cvs/{id}/file.
/// </summary>
public sealed class LocalCvFileStorage : ICvFileStorage, ICvErasureStorage
{
    private readonly string _contentRoot;   // thư mục gốc của Api, ví dụ D:\Projects\SmartCv\src\SmartCv.Api
    private readonly string _storageRoot;   // tương đối, ví dụ "Uploads/CV"
    private readonly ILogger _logger;

    public LocalCvFileStorage(string contentRoot, string storageRoot, ILogger<LocalCvFileStorage>? logger = null)
    {
        _logger = logger ?? NullLogger<LocalCvFileStorage>.Instance;
        _contentRoot = Path.GetFullPath(contentRoot);
        _storageRoot = storageRoot.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(_storageRoot) || _storageRoot.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException($"Thư mục lưu CV không hợp lệ: \"{storageRoot}\".", nameof(storageRoot));
    }

    public async Task<string> SaveAsync(Guid cvId, string extension, Stream content, DateTime uploadedAtUtc, CancellationToken cancellationToken = default)
    {
        // Đuôi file đã được A5 kiểm tra; chặn thêm ở đây để không ghép được ký tự đường dẫn vào tên file
        if (string.IsNullOrEmpty(extension) || extension[0] != '.' || extension.Length > 10
            || extension.IndexOfAny(['/', '\\', ':']) >= 0 || extension.Contains(".."))
            throw new ArgumentException($"Đuôi file không hợp lệ: \"{extension}\".", nameof(extension));

        // Đường dẫn lưu vào database luôn dùng dấu "/", giống dữ liệu seed: Uploads/CV/2026/11/<GUID>.pdf
        var storedPath = $"{_storageRoot}/{uploadedAtUtc:yyyy}/{uploadedAtUtc:MM}/{cvId}{extension.ToLowerInvariant()}";
        var fullPath = ToFullPath(storedPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // FileMode.CreateNew: báo lỗi nếu file đã tồn tại (không bao giờ ghi đè file có sẵn).
        // Lỗi ở bước này thì chưa tạo file nào của lần lưu này, nên không xóa gì.
        var file = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        try
        {
            await using (file)
            {
                await content.CopyToAsync(file, cancellationToken);
            }
        }
        catch
        {
            // Ghi dở (lỗi đọc luồng, hết dung lượng, hủy request...): xóa file vừa tạo, không để file hỏng / mồ côi
            TryDeleteFullPath(fullPath, storedPath);
            throw;
        }

        return storedPath;
    }

    public Stream OpenRead(string storedPath) =>
        new FileStream(ToFullPath(storedPath), FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

    public bool Exists(string storedPath) => File.Exists(ToFullPath(storedPath));

    public void Delete(string storedPath)
    {
        var fullPath = ToFullPath(storedPath);
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }

    // Recovery copies are private, deterministic by CV ID and never served by any HTTP endpoint.
    // A crash before SQL commit is repaired on retry; a crash after commit is purged on retry.
    private string RecoveryPath(Guid id) => ToFullPath($"{_storageRoot}/.a39-recovery/{id:N}");

    private string ErasurePath(string storedPath)
    {
        var full = ToFullPath(storedPath);
        var root = Path.GetFullPath(Path.Combine(_contentRoot, _storageRoot));
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Không xử lý file qua liên kết hệ thống.");
            if (string.Equals(current, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return full;
        }
        throw new IOException("Đường dẫn lưu CV không hợp lệ.");
    }

    public IReadOnlyList<Guid> PendingErasureIds(int limit)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        var directory = Path.GetDirectoryName(ErasurePath($"{_storageRoot}/.a39-recovery/entry"))!;
        try
        {
            return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(limit)
                .Select(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty).ToArray();
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    public bool PrepareErasure(Guid cvId, string storedPath)
    {
        var original = ErasurePath(storedPath);
        var backup = RecoveryPath(cvId);
        ErasurePath($"{_storageRoot}/.a39-recovery/{cvId:N}");
        // A retained journal belongs to a previously interrupted attempt for this exact CV/path.
        if (File.Exists(backup + ".json"))
        {
            var recorded = System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(backup + ".json"));
            if (recorded != storedPath) throw new IOException("Cần kiểm tra bản phục hồi CV.");
            RollbackErasure(cvId);
        }
        FileStream input;
        try { input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        using (input)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            using var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        using (var journal = new FileStream(backup + ".json", FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(storedPath);
            journal.Write(bytes);
            journal.Flush(flushToDisk: true);
        }
        File.Delete(original);
        return true;
    }

    public void RollbackErasure(Guid cvId)
    {
        var backup = RecoveryPath(cvId);
        ErasurePath($"{_storageRoot}/.a39-recovery/{cvId:N}");
        if (!File.Exists(backup + ".json")) return;
        var stored = System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(backup + ".json"))
            ?? throw new IOException("Bản phục hồi không hợp lệ.");
        var original = ErasurePath(stored);
        if (!File.Exists(original)) File.Copy(backup, original, overwrite: false);
        File.Delete(backup);
        File.Delete(backup + ".json");
    }

    public void CompleteErasure(Guid cvId)
    {
        var backup = RecoveryPath(cvId);
        ErasurePath($"{_storageRoot}/.a39-recovery/{cvId:N}");
        if (File.Exists(backup + ".json"))
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(backup + ".json"))
                ?? throw new IOException("Bản phục hồi không hợp lệ.");
            DeleteMissingAllowed(ErasurePath(stored));
        }
        DeleteMissingAllowed(backup);
        DeleteMissingAllowed(backup + ".json");
    }

    private static void DeleteMissingAllowed(string path)
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { } // missing file/directory is already erased; permission and I/O still propagate
    }

    /// <summary>Đổi đường dẫn tương đối sang đường dẫn đầy đủ, và chặn mọi đường dẫn thoát ra ngoài thư mục gốc.</summary>
    private string ToFullPath(string storedPath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_contentRoot, storedPath.Replace('/', Path.DirectorySeparatorChar)));
        var allowedRoot = Path.GetFullPath(Path.Combine(_contentRoot, _storageRoot)) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(allowedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Đường dẫn file CV không hợp lệ.");

        return fullPath;
    }

    private void TryDeleteFullPath(string fullPath, string storedPath)
    {
        try { if (File.Exists(fullPath)) File.Delete(fullPath); }
        catch (Exception ex)
        {
            // Không che lỗi gốc của bên gọi; ghi log để quản trị xóa tay file ghi dở (tên GUID, không lộ tên ứng viên)
            _logger.LogError(ex, "Không xóa được file CV ghi dở {StoredPath}; cần xóa thủ công", storedPath);
        }
    }
}
