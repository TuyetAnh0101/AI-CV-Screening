using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Constants;
using SmartCv.Core.Extraction;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Extraction;

/// <summary>
/// Đọc chữ cho MỘT CV rồi ghi kết quả vào bảng Cvs:
///   ExtractedText, ExtractionMethod, ExtractionStatus (Completed / Failed), ExtractionError.
/// Đăng ký Scoped (dùng DbContext). Worker chạy nền tạo scope mới cho mỗi CV.
/// </summary>
public sealed class CvExtractionProcessor(
    SmartCvDbContext db,
    ICvFileStorage storage,
    ICvTextExtractor extractor,
    ILogger<CvExtractionProcessor> logger)
{
    private const int MaxErrorLength = 1000;   // cột ExtractionError NVARCHAR(1000)
    public const string FileMissingError = "Không tìm thấy file gốc trên máy chủ.";

    public async Task ProcessAsync(Guid cvId, CancellationToken ct)
    {
        var cv = await db.Cvs.FirstOrDefaultAsync(c => c.CvId == cvId, ct);
        if (cv is null)
        {
            logger.LogWarning("Không tìm thấy CV {CvId} để đọc chữ", cvId);
            return;
        }

        // Chỉ xử lý CV đang chờ (tránh đọc lại CV đã xong khi bị xếp hàng 2 lần)
        if (cv.ExtractionStatus != ExtractionStatuses.Pending) return;

        var started = DateTime.UtcNow;
        TextExtractionResult result;

        try
        {
            await using var stream = storage.OpenRead(cv.StoredPath);
            result = await extractor.ExtractAsync(stream, cv.ContentType, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            // InvalidOperationException: StoredPath nằm ngoài thư mục lưu CV (LocalCvFileStorage chặn)
            result = TextExtractionResult.Failure(FileMissingError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Lỗi không mong đợi khi đọc chữ CV {CvId}", cvId);
            result = TextExtractionResult.Failure("Lỗi không mong đợi khi đọc file. Vui lòng thử đọc lại.");
        }

        cv.ExtractionMethod = result.Method;
        if (result.IsSuccess)
        {
            cv.ExtractedText = result.Text;
            cv.ExtractionStatus = ExtractionStatuses.Completed;
            cv.ExtractionError = null;
        }
        else
        {
            cv.ExtractedText = null;
            cv.ExtractionStatus = ExtractionStatuses.Failed;
            cv.ExtractionError = Truncate(result.Error, MaxErrorLength);
        }

        // Đã đọc xong: ghi kết quả kể cả khi API đang tắt, để CV không phải đọc lại từ đầu
        await db.SaveChangesAsync(CancellationToken.None);

        // Không ghi tên file / nội dung CV vào log (dữ liệu cá nhân của ứng viên)
        logger.LogInformation("Đọc chữ CV {CvId}: {Status} bằng {Method}, {Chars} ký tự, {Ms} ms",
            cvId, cv.ExtractionStatus, cv.ExtractionMethod ?? "-",
            cv.ExtractedText?.Length ?? 0, (int)(DateTime.UtcNow - started).TotalMilliseconds);
    }

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..(max - 1)] + "…";
}
