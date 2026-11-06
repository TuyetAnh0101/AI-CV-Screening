namespace SmartCv.Core.Options;

/// <summary>
/// Cấu hình tải CV, đọc từ mục "CvUpload" trong appsettings.json.
/// Đổi giới hạn chỉ cần sửa appsettings, không phải sửa code.
/// </summary>
public sealed class CvUploadOptions
{
    public const string SectionName = "CvUpload";

    /// <summary>Dung lượng tối đa của MỘT file (MB). A5 dùng để từ chối file quá lớn.</summary>
    public int MaxFileSizeMB { get; set; } = 10;

    /// <summary>Số file tối đa trong MỘT lần gửi.</summary>
    public int MaxFilesPerRequest { get; set; } = 20;

    /// <summary>Đuôi file được phép (A5 dùng).</summary>
    public string[] AllowedExtensions { get; set; } = [".pdf", ".png", ".jpg", ".jpeg"];

    /// <summary>Thư mục gốc lưu file (A6 dùng), tính từ thư mục chạy của Api.</summary>
    public string StorageRoot { get; set; } = "Uploads/CV";

    public long MaxFileSizeBytes => MaxFileSizeMB * 1024L * 1024L;

    /// <summary>
    /// Giới hạn dung lượng của cả request = (dung lượng 1 file × số file) + 1 MB dự phòng cho phần đầu multipart.
    /// Vượt mức này, máy chủ trả lỗi 413 trước khi kịp đọc file.
    /// </summary>
    public long MaxRequestBytes => MaxFileSizeBytes * MaxFilesPerRequest + 1024L * 1024L;
}
