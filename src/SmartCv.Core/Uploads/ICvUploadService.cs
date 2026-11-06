using SmartCv.Core.Contracts;

namespace SmartCv.Core.Uploads;

/// <summary>
/// Xử lý các file CV được tải lên.
/// A4: nhận file và trả thông tin từng file.
/// A5: kiểm tra định dạng, dung lượng, file trùng (hash SHA-256).
/// A6: lưu file vào thư mục Uploads/CV/yyyy/MM/&lt;GUID&gt;.ext và ghi bảng Cvs.
/// </summary>
public interface ICvUploadService
{
    Task<CvUploadResponse> UploadAsync(IReadOnlyList<CvUploadFile> files, CancellationToken cancellationToken = default);
}
