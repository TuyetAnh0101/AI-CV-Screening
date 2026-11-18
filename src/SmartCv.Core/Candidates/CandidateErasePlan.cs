using SmartCv.Core.Constants;

namespace SmartCv.Core.Candidates;

/// <summary>Hồ sơ cần rút trước khi xóa dữ liệu.</summary>
public sealed record WithdrawStep(int ApplicationId, string FromStage);

/// <summary>
/// A43: kế hoạch xử lý yêu cầu XÓA dữ liệu của ứng viên. Hàm thuần → unit test được.
///   1) Mọi hồ sơ chưa ở "Loại" (Mới, Đang xem, Qua vòng CV, Phỏng vấn, Dự phòng) → chuyển "Loại" với lý do
///      "Ứng viên rút hồ sơ" (CandidateWithdrew). Ứng viên đã yêu cầu xóa thì không thể tiếp tục tuyển.
///      Hồ sơ đã "Loại" giữ nguyên lý do cũ (không viết lại lịch sử).
///   2) Ẩn danh hóa TẤT CẢ CV (dùng lại A39, bỏ qua quy tắc chặn giai đoạn vì bước 1 đã rút hồ sơ).
///   3) Ghi nhật ký với lý do "theo yêu cầu ứng viên".
/// </summary>
public static class CandidateErasePlan
{
    public const string Reason = "Theo yêu cầu ứng viên";
    public const string EventNote = "Theo yêu cầu ứng viên: xóa dữ liệu cá nhân";
    public const int MaxNoteLength = 500;
    public const int MaxHandledByLength = 100;

    public static List<WithdrawStep> Withdrawals(IEnumerable<(int ApplicationId, string Stage)> applications) =>
        applications
            .Where(a => a.Stage != ApplicationStages.Rejected)
            .Select(a => new WithdrawStep(a.ApplicationId, a.Stage))
            .ToList();

    /// <summary>Kiểm tra body. Trả lỗi theo tên trường (rỗng = hợp lệ).</summary>
    public static Dictionary<string, string[]> Validate(bool confirm, string? handledBy, string? note)
    {
        var errors = new Dictionary<string, string[]>();
        if (!confirm) errors["confirm"] = ["Cần xác nhận (confirm = true). Thao tác này không hoàn tác được."];
        if (string.IsNullOrWhiteSpace(handledBy)) errors["handledBy"] = ["Cần ghi ai xử lý yêu cầu."];
        else if (handledBy.Trim().Length > MaxHandledByLength) errors["handledBy"] = [$"Tối đa {MaxHandledByLength} ký tự."];
        if (handledBy?.Any(char.IsControl) == true) errors["handledBy"] = ["Người xử lý không được chứa ký tự điều khiển."];
        if (note is { Length: > MaxNoteLength }) errors["note"] = [$"Ghi chú tối đa {MaxNoteLength} ký tự."];
        return errors;
    }

    /// <summary>Câu tóm tắt lưu vào nhật ký, ví dụ "Theo yêu cầu ứng viên: ẩn danh hóa 2 CV, rút 1 hồ sơ, xóa thông tin liên hệ".</summary>
    public static string Summary(int cvsAnonymized, int cvsAlready, int withdrawn, bool candidateDeleted)
    {
        var parts = new List<string> { $"ẩn danh hóa {cvsAnonymized} CV" };
        if (cvsAlready > 0) parts.Add($"{cvsAlready} CV đã ẩn danh từ trước");
        if (withdrawn > 0) parts.Add($"rút {withdrawn} hồ sơ");
        if (candidateDeleted) parts.Add("xóa thông tin liên hệ");
        return $"{Reason}: {string.Join(", ", parts)}";
    }
}
