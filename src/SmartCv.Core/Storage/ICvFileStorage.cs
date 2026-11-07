namespace SmartCv.Core.Storage;

/// <summary>
/// Nơi lưu file CV gốc. Hiện lưu vào thư mục trên máy chủ (LocalCvFileStorage).
/// Sau này muốn chuyển lên dịch vụ lưu trữ đám mây thì chỉ viết class mới, không sửa code dùng nó.
/// </summary>
public interface ICvFileStorage
{
    /// <summary>
    /// Lưu file, trả về đường dẫn tương đối để ghi vào cột Cvs.StoredPath,
    /// ví dụ "Uploads/CV/2026/11/3f2a...e1.pdf".
    /// </summary>
    Task<string> SaveAsync(Guid cvId, string extension, Stream content, DateTime uploadedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Mở file để đọc. Ném FileNotFoundException nếu file không còn.</summary>
    Stream OpenRead(string storedPath);

    bool Exists(string storedPath);

    /// <summary>Xóa file (dùng khi lưu database thất bại). Không báo lỗi nếu file không tồn tại.</summary>
    void Delete(string storedPath);
}
