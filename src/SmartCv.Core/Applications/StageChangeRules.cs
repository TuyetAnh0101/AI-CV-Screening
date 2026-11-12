using SmartCv.Core.Constants;

namespace SmartCv.Core.Applications;

/// <summary>Yêu cầu đổi giai đoạn (đã chuẩn hóa: cắt khoảng trắng, chuỗi rỗng → null).</summary>
public sealed record StageChange(string ToStage, string? RejectReason, string? Note, string Actor);

/// <summary>Trạng thái hiện tại của một hồ sơ.</summary>
public sealed record CurrentStage(int ApplicationId, string Stage, string? RejectReason);

public enum StageChangeOutcome
{
    Changed,     // sẽ đổi và ghi đúng MỘT dòng lịch sử
    Unchanged,   // đã ở đúng giai đoạn (và đúng lý do) → không ghi lịch sử
    NotFound     // không có hồ sơ này
}

public sealed record StageChangePlan(int ApplicationId, StageChangeOutcome Outcome, string? FromStage, string? FromRejectReason);

/// <summary>
/// A35: quy tắc đổi giai đoạn hồ sơ. Hàm thuần (không đọc database) → unit test được.
///   - 6 giai đoạn; từ BẤT KỲ giai đoạn nào cũng chuyển được sang giai đoạn khác (HR quyết định, kể cả mở lại hồ sơ đã Loại).
///     Không ép thứ tự cứng: thực tế HR hay bỏ qua "Đang xem" hoặc sửa nhầm; lịch sử đã ghi lại mọi bước.
///   - Loại (Rejected) BẮT BUỘC có lý do trong 6 lý do; giai đoạn khác KHÔNG được kèm lý do loại.
///   - Người thao tác (Actor) bắt buộc (trước M6 lấy tên HR nhập; sau M6 lấy từ tài khoản).
///   - Đã đúng giai đoạn và đúng lý do → Unchanged, không sinh dòng lịch sử.
///   - Loại → Loại nhưng ĐỔI lý do → Changed (có ghi lịch sử).
/// </summary>
public static class StageChangeRules
{
    public const int MaxNoteLength = 500;
    public const int MaxActorLength = 100;
    public const int MaxBulkCount = 200;

    /// <summary>Kiểm tra và chuẩn hóa. Trả (thay đổi hợp lệ, null) hoặc (null, thông báo lỗi tiếng Việt).</summary>
    public static (StageChange? Change, string? Error) Validate(string? toStage, string? rejectReason, string? note, string? actor)
    {
        var to = Clean(toStage);
        var reason = Clean(rejectReason);
        var n = Clean(note);
        var a = Clean(actor);

        if (to is null) return (null, "Chưa chọn giai đoạn mới.");
        if (!ApplicationStages.IsValid(to))
            return (null, $"Giai đoạn \"{to}\" không hợp lệ. Dùng: {string.Join(", ", ApplicationStages.All)}.");

        if (to == ApplicationStages.Rejected)
        {
            if (reason is null) return (null, "Loại hồ sơ phải chọn lý do.");
            if (!RejectReasons.IsValid(reason))
                return (null, $"Lý do \"{reason}\" không hợp lệ. Dùng: {string.Join(", ", RejectReasons.All)}.");
        }
        else if (reason is not null)
        {
            return (null, "Chỉ chọn lý do khi Loại hồ sơ.");
        }

        if (a is null) return (null, "Thiếu tên người thao tác.");
        if (a.Length > MaxActorLength) return (null, $"Tên người thao tác tối đa {MaxActorLength} ký tự.");
        if (n is { Length: > MaxNoteLength }) return (null, $"Ghi chú tối đa {MaxNoteLength} ký tự.");

        return (new StageChange(to, reason, n, a), null);
    }

    /// <summary>Có cần đổi không (cùng giai đoạn và cùng lý do → không).</summary>
    public static bool IsNoOp(CurrentStage current, StageChange change) =>
        current.Stage == change.ToStage && current.RejectReason == change.RejectReason;

    /// <summary>Kiểm tra danh sách hồ sơ cho thao tác hàng loạt.</summary>
    public static string? ValidateIds(IReadOnlyCollection<int>? ids)
    {
        if (ids is null || ids.Count == 0) return "Chưa chọn hồ sơ nào.";
        if (ids.Count > MaxBulkCount) return $"Tối đa {MaxBulkCount} hồ sơ mỗi lần.";
        if (ids.Any(i => i <= 0)) return "Mã hồ sơ phải là số nguyên dương.";
        if (ids.Distinct().Count() != ids.Count) return "Danh sách hồ sơ bị trùng.";
        return null;
    }

    /// <summary>
    /// Lập kế hoạch cho từng hồ sơ (giữ đúng thứ tự gửi lên).
    /// Hồ sơ không tồn tại → NotFound (các hồ sơ khác vẫn đổi); đã đúng giai đoạn → Unchanged.
    /// </summary>
    public static List<StageChangePlan> Plan(IEnumerable<int> requestedIds, IReadOnlyDictionary<int, CurrentStage> current, StageChange change) =>
        requestedIds.Select(id =>
            !current.TryGetValue(id, out var c)
                ? new StageChangePlan(id, StageChangeOutcome.NotFound, null, null)
                : new StageChangePlan(id, IsNoOp(c, change) ? StageChangeOutcome.Unchanged : StageChangeOutcome.Changed, c.Stage, c.RejectReason))
        .ToList();

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
