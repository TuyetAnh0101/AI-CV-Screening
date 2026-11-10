using SmartCv.Core.Contracts;
using SmartCv.Web.Models;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B3: phần logic thuần của trang tải CV (không phụ thuộc giao diện, có unit test).
/// (1) Chia file thành nhiều request để không request nào vượt giới hạn số file / tổng dung lượng của API (A4).
/// (2) Đổi kết quả API thành trạng thái từng dòng trong bảng.
/// </summary>
public static class UploadFlow
{
    /// <summary>Chia theo thứ tự: mỗi nhóm tối đa <paramref name="maxFiles"/> file và tổng tối đa <paramref name="maxBytes"/> byte.</summary>
    public static List<List<T>> SplitIntoBatches<T>(IReadOnlyList<T> files, Func<T, long> sizeOf, int maxFiles, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        var batches = new List<List<T>>();
        var current = new List<T>();
        long currentBytes = 0;

        foreach (var file in files)
        {
            var size = sizeOf(file);
            if (current.Count > 0 && (current.Count >= maxFiles || currentBytes + size > maxBytes))
            {
                batches.Add(current);
                current = [];
                currentBytes = 0;
            }
            current.Add(file);
            currentBytes += size;
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>
    /// Cập nhật trạng thái các dòng của một request. Chỉ "Accepted" có CvId mới là Hoàn thành;
    /// "Received" (chưa lưu), lỗi kết nối hay thiếu kết quả đều KHÔNG được coi là thành công.
    /// </summary>
    public static void ApplyResult(IReadOnlyList<UploadItem> batch, ApiResult<CvUploadResponse> result)
    {
        if (!result.IsSuccess || result.Data is null)
        {
            // B5: lỗi máy chủ (5xx) không hiện chi tiết kỹ thuật của API cho người dùng.
            var message = result.StatusCode >= 500
                ? ServerErrorRowMessage
                : result.ErrorMessage ?? "Không tải lên được. Vui lòng thử lại.";
            foreach (var item in batch) Fail(item, message, errorCode: null);
            return;
        }

        var files = result.Data.Files;
        for (var i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            // API trả kết quả theo đúng thứ tự file gửi lên (A4–A6); kiểm tra thêm tên để không gán nhầm dòng.
            var r = i < files.Count && files[i].FileName == item.FileName
                ? files[i]
                : files.FirstOrDefault(f => f.FileName == item.FileName);

            if (r is null) { Fail(item, "API không trả kết quả cho file này.", errorCode: null); continue; }

            if (r.Status == CvUploadStatuses.Accepted && r.CvId is { } id && id != Guid.Empty)
            {
                item.State = UploadItemState.Uploaded;
                item.CvId = id;
                item.Message = null;
                item.ErrorCode = null;
            }
            else if (r.Status == CvUploadStatuses.Rejected && r.ErrorCode == CvUploadErrorCodes.Duplicate
                     && r.CvId is { } oldId && oldId != Guid.Empty)
            {
                // Trùng CV đã có và API trả CvId cũ: dùng lại để ứng tuyển công việc khác.
                item.State = UploadItemState.Existing;
                item.CvId = oldId;
                item.Message = r.ErrorMessage ?? "CV này đã có trong hệ thống.";
                item.ErrorCode = r.ErrorCode;
            }
            else if (r.Status == CvUploadStatuses.Rejected)
            {
                // Trùng nhưng KHÔNG có CvId: không tạo mã giả, không cho phân tích (xem RejectedHint).
                Fail(item, string.IsNullOrWhiteSpace(r.ErrorMessage) ? DefaultMessage(r.ErrorCode) : r.ErrorMessage, r.ErrorCode);
            }
            else
            {
                Fail(item, "Máy chủ chưa lưu CV (không có CvId).", r.ErrorCode);
            }
        }
    }

    public const string ServerErrorRowMessage = "Máy chủ gặp lỗi khi xử lý file. Vui lòng thử lại sau.";

    /// <summary>Thông báo mặc định theo mã lỗi khi API không gửi kèm ErrorMessage.</summary>
    public static string DefaultMessage(string? errorCode) => errorCode switch
    {
        CvUploadErrorCodes.EmptyFile => "File rỗng (0 byte).",
        CvUploadErrorCodes.InvalidFormat => "Nội dung file không phải PDF, JPG hoặc PNG hợp lệ (có thể bị đổi đuôi hoặc bị hỏng).",
        CvUploadErrorCodes.TooLarge => "File vượt dung lượng cho phép.",
        CvUploadErrorCodes.Duplicate => "CV này đã có trong hệ thống.",
        CvUploadErrorCodes.SaveFailed => "Máy chủ chưa lưu được file. Vui lòng thử lại.",
        _ => "API từ chối file này."
    };

    /// <summary>
    /// Dòng gợi ý thêm dưới lý do lỗi. Trùng CV trong database nhưng API không trả CvId cũ
    /// (API chưa làm mục A.2 của hướng dẫn B3–B6) → nói rõ vì sao chưa chọn được để phân tích.
    /// </summary>
    public static string? RejectedHint(UploadItem item) =>
        item.State == UploadItemState.Rejected && item.ErrorCode == CvUploadErrorCodes.Duplicate && item.CvId is null
            ? "Hệ thống chưa trả mã CV cũ nên chưa thể chọn file này để phân tích."
            : null;

    /// <summary>
    /// Khung báo lỗi đầu thẻ khi CẢ request tải lên thất bại (không phải lỗi của từng file).
    /// StatusCode null = không tới được API (mất kết nối, quá thời gian chờ, dữ liệu hỏng).
    /// </summary>
    public static (string Title, string Message)? DescribeBatchFailure<T>(ApiResult<T> result)
    {
        if (result.IsSuccess) return null;
        return result.StatusCode switch
        {
            null => ("Không kết nối được máy chủ API",
                     (result.ErrorMessage ?? "Không gửi được file.") + " Các file chưa được lưu, hãy tải lại sau khi kết nối ổn định."),
            >= 500 => ("Máy chủ API đang gặp lỗi", "Các file chưa được lưu. Vui lòng thử lại sau ít phút."),
            _ => ("API từ chối yêu cầu tải lên", result.ErrorMessage ?? "Yêu cầu tải lên không hợp lệ.")
        };
    }

    /// <summary>Thông báo tổng kết sau một lần tải: số CV mới, đã có sẵn, bị từ chối, lỗi file chưa gửi.</summary>
    public static (string Text, bool HasErrors) Summarize(IReadOnlyCollection<UploadItem> sent, int invalidCount)
    {
        if (sent.Count == 0)
            return ($"Không có file hợp lệ để tải lên ({invalidCount} file lỗi, xem lý do trong danh sách).", true);

        var uploaded = sent.Count(i => i.State == UploadItemState.Uploaded);
        var existing = sent.Count(i => i.State == UploadItemState.Existing);
        var rejected = sent.Count(i => i.State == UploadItemState.Rejected);

        var parts = new List<string> { $"{uploaded} CV mới" };
        if (existing > 0) parts.Add($"{existing} CV đã có sẵn");
        if (rejected > 0) parts.Add($"{rejected} file bị từ chối");
        if (invalidCount > 0) parts.Add($"{invalidCount} file lỗi chưa gửi");

        var hasErrors = rejected > 0 || invalidCount > 0;
        return ($"Kết quả tải lên: {string.Join(", ", parts)}." + (hasErrors ? " Xem lý do ở từng dòng trong danh sách." : string.Empty),
                hasErrors);
    }

    private static void Fail(UploadItem item, string message, string? errorCode)
    {
        item.State = UploadItemState.Rejected;
        item.CvId = null;
        item.Selected = false;
        item.Message = message;
        item.ErrorCode = errorCode;
    }
}
