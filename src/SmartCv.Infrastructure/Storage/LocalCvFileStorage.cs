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
public sealed class LocalCvFileStorage : ICvFileStorage
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

    /// <summary>Đổi đường dẫn tương đối sang đường dẫn đầy đủ, và chặn mọi đường dẫn thoát ra ngoài thư mục gốc.</summary>
    private string ToFullPath(string storedPath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_contentRoot, storedPath.Replace('/', Path.DirectorySeparatorChar)));
        var allowedRoot = Path.GetFullPath(Path.Combine(_contentRoot, _storageRoot)) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
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
