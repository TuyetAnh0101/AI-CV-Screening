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
            foreach (var item in batch) Fail(item, result.ErrorMessage ?? "Không tải lên được. Vui lòng thử lại.");
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

            if (r is null) { Fail(item, "API không trả kết quả cho file này."); continue; }

            if (r.Status == CvUploadStatuses.Accepted && r.CvId is { } id && id != Guid.Empty)
            {
                item.State = UploadItemState.Uploaded;
                item.CvId = id;
                item.Message = null;
            }
            else if (r.Status == CvUploadStatuses.Rejected && r.ErrorCode == CvUploadErrorCodes.Duplicate
                     && r.CvId is { } oldId && oldId != Guid.Empty)
            {
                item.State = UploadItemState.Existing;
                item.CvId = oldId;
                item.Message = r.ErrorMessage;
            }
            else if (r.Status == CvUploadStatuses.Rejected)
            {
                Fail(item, r.ErrorMessage ?? "API từ chối file này.");
            }
            else
            {
                Fail(item, "Máy chủ chưa lưu CV (không có CvId).");
            }
        }
    }

    private static void Fail(UploadItem item, string message)
    {
        item.State = UploadItemState.Rejected;
        item.CvId = null;
        item.Selected = false;
        item.Message = message;
    }
}
