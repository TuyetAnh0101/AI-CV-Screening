using System.Globalization;
using System.Text;
using SmartCv.Core.Candidates;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

/// <summary>Hậu quả của việc xóa dữ liệu, hiện ở bước 1 của hộp thoại xác nhận.</summary>
public sealed record ErasePreview(int CvsToAnonymize, int CvsAlreadyAnonymized, int ApplicationsToWithdraw, IReadOnlyList<string> JobsWithdrawn);

/// <summary>Hậu quả của việc gộp: ai được giữ, bao nhiêu CV / hồ sơ ứng tuyển chuyển sang, liên hệ nào được bổ sung.</summary>
public sealed record MergePreview(int KeepId, int RemoveId, int CvsMoved, int ApplicationsMoved, bool EmailFilled, bool PhoneFilled);

/// <summary>B24: chữ hiển thị trang Ứng viên. Hàm thuần → unit test được.</summary>
public static class CandidateText
{
    /// <summary>Chữ HR phải gõ ở bước 2 để bật nút xóa.</summary>
    public const string EraseKeyword = "XÓA";

    /// <summary>"0912345678" → "0912 345 678"; khác 10 số → giữ nguyên; null → "—".</summary>
    public static string Phone(string? phone) =>
        phone is { Length: 10 } p && p.All(char.IsDigit) ? $"{p[..4]} {p[4..7]} {p[7..]}" : string.IsNullOrWhiteSpace(phone) ? "—" : phone;

    /// <summary>Tên hiển thị: khi bật Ẩn danh chỉ còn mã người (không tên, không chữ cái đầu).</summary>
    public static string Name(int candidateId, string? fullName, bool anonymous) =>
        anonymous ? Alias(candidateId) : string.IsNullOrWhiteSpace(fullName) ? $"Ứng viên #{candidateId}" : fullName;
    public static string Alias(int candidateId) => $"Ứng viên #{candidateId}";

    /// <summary>Chữ cái đầu cho avatar; Ẩn danh → "#".</summary>
    public static string Initials(string? fullName, bool anonymous)
    {
        if (anonymous || string.IsNullOrWhiteSpace(fullName)) return "#";
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = parts[0][0]; var last = parts.Length > 1 ? parts[^1][0] : '\0';
        return (last == '\0' ? first.ToString() : $"{first}{last}").ToUpper(CultureInfo.GetCultureInfo("vi-VN"));
    }

    /// <summary>Gõ "XÓA", "xóa", "XOA" (không dấu), có khoảng trắng hai đầu đều nhận. Gõ khác → chưa cho xóa.</summary>
    public static bool IsEraseConfirmed(string? typed) =>
        !string.IsNullOrWhiteSpace(typed) && Fold(typed.Trim()) == Fold(EraseKeyword);

    /// <summary>Dùng đúng quy tắc của API (CandidateErasePlan) để báo trước: bao nhiêu CV bị ẩn danh, bao nhiêu hồ sơ bị rút.</summary>
    public static ErasePreview Preview(CandidateDetailDto c)
    {
        var withdraw = CandidateErasePlan.Withdrawals(c.Applications.Select(a => (a.ApplicationId, a.Stage)))
            .Select(w => w.ApplicationId).ToHashSet();
        return new ErasePreview(
            c.Cvs.Count(v => !v.IsAnonymized),
            c.Cvs.Count(v => v.IsAnonymized),
            withdraw.Count,
            c.Applications.Where(a => withdraw.Contains(a.ApplicationId)).Select(a => a.JobTitle).Distinct().ToList());
    }

    /// <summary>
    /// Gộp theo contract A24: mọi CV của người bị gộp (và các hồ sơ ứng tuyển gắn với CV đó) chuyển sang người được giữ;
    /// email / số điện thoại chỉ được bổ sung khi người được giữ đang thiếu; người bị gộp bị xóa.
    /// </summary>
    public static MergePreview Merge(int keepId, string? keepEmail, string? keepPhone, int removeId, string? removeEmail, string? removePhone, int removeCvs, int removeApplications) =>
        new(keepId, removeId, removeCvs, removeApplications,
            string.IsNullOrWhiteSpace(keepEmail) && !string.IsNullOrWhiteSpace(removeEmail),
            string.IsNullOrWhiteSpace(keepPhone) && !string.IsNullOrWhiteSpace(removePhone));

    /// <summary>StageTimeline vẽ mới nhất trước; API trả cũ → mới.</summary>
    public static List<ApplicationEventDto> NewestFirst(IEnumerable<ApplicationEventDto> events) =>
        events.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.EventId).ToList();

    /// <summary>"Trùng email và điện thoại" · "Trùng email"</summary>
    public static string MatchedOn(IEnumerable<string> on)
    {
        var parts = on.Select(o => o switch { "Email" => "email", "Phone" => "điện thoại", _ => "liên hệ" }).Distinct().ToList();
        return parts.Count == 0 ? "" : "Trùng " + string.Join(" và ", parts);
    }

    /// <summary>"2 CV · 3 hồ sơ ứng tuyển · 2 vị trí" — tóm tắt đầu trang, không gộp điểm thành "điểm của người".</summary>
    public static string Summary(CandidateDetailDto c)
    {
        var jobs = c.Applications.Select(a => a.JobId).Distinct().Count();
        return $"{c.Cvs.Count} CV · {c.Applications.Count} hồ sơ ứng tuyển · {jobs} vị trí";
    }

    /// <summary>Chữ cố định cho mọi lỗi API ứng viên; không bao giờ hiện thông báo thô của máy chủ.</summary>
    public static string Error(int? status, bool writing = false) => status switch
    {
        400 => "Dữ liệu chưa hợp lệ. Hãy kiểm tra các ô được đánh dấu rồi thử lại.",
        401 or 403 => "Bạn chưa có quyền thực hiện thao tác này.",
        404 => "Không tìm thấy ứng viên. Hồ sơ có thể đã được gộp hoặc xóa; hãy tải lại.",
        409 => "Dữ liệu vừa thay đổi hoặc trùng người khác. Hãy tải lại và kiểm tra trước khi thao tác tiếp.",
        410 => "Dữ liệu của ứng viên này đã được xóa theo yêu cầu.",
        429 => "Đang có nhiều yêu cầu. Vui lòng chờ một lát rồi thử lại.",
        503 when writing => "Máy chủ đang bận và chưa xác nhận kết quả. Hãy tải lại để kiểm tra trước khi gửi lại.",
        503 => "Máy chủ API tạm thời không sẵn sàng. Vui lòng thử lại sau ít phút.",
        >= 500 when writing => "Máy chủ gặp lỗi và chưa xác nhận kết quả. Hãy tải lại để kiểm tra trước khi gửi lại.",
        >= 500 => "Máy chủ gặp lỗi khi tải dữ liệu ứng viên. Vui lòng thử lại sau.",
        null when writing => "Chưa xác nhận được kết quả lưu (mất kết nối hoặc quá thời gian). Hãy tải lại để kiểm tra trước khi gửi lại.",
        null => "Không kết nối được API hoặc đã quá thời gian chờ. Vui lòng thử lại.",
        _ => "Chưa xử lý được yêu cầu. Vui lòng thử lại."
    };

    /// <summary>"12/11/2026" theo giờ Việt Nam; null → "—".</summary>
    public static string Date(DateTime? utc) => utc is { } d ? DateOnly.FromDateTime(DateTime.SpecifyKind(d, DateTimeKind.Utc).AddHours(7)).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "—";

    private static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC).Replace('đ', 'd').Replace('Đ', 'D').ToUpperInvariant();
    }
}
