using Microsoft.Extensions.Logging;
using SmartCv.Core.Contracts;
using SmartCv.Core.Uploads;

namespace SmartCv.Infrastructure.Uploads;

/// <summary>
/// A4: nhận danh sách file và trả lại thông tin từng file với trạng thái Received.
/// Chưa kiểm tra (A5) và chưa lưu (A6). Hai việc đó sẽ được thêm vào đúng các chỗ đánh dấu TODO bên dưới,
/// nên API và giao diện không phải thay đổi.
/// </summary>
public sealed class CvUploadService(ILogger<CvUploadService> logger) : ICvUploadService
{
    public Task<CvUploadResponse> UploadAsync(IReadOnlyList<CvUploadFile> files, CancellationToken cancellationToken = default)
    {
        var results = new List<CvUploadItemResult>(files.Count);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Chỉ giữ tên file, bỏ phần đường dẫn mà một số trình duyệt cũ gửi kèm (vd: C:\Users\...\cv.pdf).
            // Đổi '\' thành '/' trước để bỏ được đường dẫn kiểu Windows cả khi API chạy trên Linux.
            var safeName = Path.GetFileName(file.FileName.Replace('\\', '/'));

            // TODO A5: kiểm tra file rỗng, đuôi file, dung lượng, nội dung thật (magic bytes), hash SHA-256 trùng
            // TODO A6: lưu vào Uploads/CV/yyyy/MM/<GUID>.<ext>, ghi bảng Cvs, trả CvId và Status = Accepted

            results.Add(new CvUploadItemResult
            {
                FileName = safeName,
                SizeBytes = file.Length,
                ContentType = file.ContentType,
                Status = CvUploadStatuses.Received
            });
        }

        var failed = results.Count(r => r.Status == CvUploadStatuses.Rejected);

        logger.LogInformation("Nhận {Total} file CV: {Success} thành công, {Failed} bị từ chối",
            results.Count, results.Count - failed, failed);

        return Task.FromResult(new CvUploadResponse
        {
            TotalFiles = results.Count,
            SuccessCount = results.Count - failed,
            FailedCount = failed,
            Files = results
        });
    }
}
