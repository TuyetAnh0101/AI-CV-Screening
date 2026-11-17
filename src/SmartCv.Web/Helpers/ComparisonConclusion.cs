using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

/// <summary>B41: trạng thái khung kết luận. Chỉ Conclusive mới được tô xanh.</summary>
public enum ConclusionKind
{
    NoFocus,          // chưa có lượt để xem
    InvalidFocus,     // lượt đang xem không có thống kê hợp lệ / không thuộc bộ CV
    NeedTwoRuns,      // bộ CV chưa có đủ hai phiên bản có lượt hoàn thành
    NoActiveBase,     // không có bản đang dùng hoặc bản đang dùng chưa có lượt hoàn thành trên bộ này
    SameRun,          // lượt đang xem chính là mốc so sánh: không gọi so sánh cùng một lượt
    Loading,
    Error,            // lỗi HTTP/404/409/kết nối
    Stale,            // API trả khác lượt hoặc số liệu vừa đổi
    NotComparable,    // IsFairComparison = false, Verdict = NotComparable, khác bộ CV, dữ liệu mâu thuẫn
    MissingInterval,  // API nói Conclusive nhưng thiếu khoảng tin cậy: không được kết luận
    Inconclusive,     // công bằng nhưng hai khoảng tin cậy chồng nhau: khung vàng
    Conclusive        // công bằng, Better/Worse và hai khoảng không chồng nhau: khung xanh
}

public sealed record ConclusionView(ConclusionKind Kind, string Title, string Message, IReadOnlyList<string> Notes)
{
    public bool IsConclusive => Kind == ConclusionKind.Conclusive;
}

/// <summary>
/// B41: chọn hai lượt cần so sánh và đọc kết quả A17/A41 thành một khung kết luận.
/// Không tự tạo verdict bằng cách so % và không ép Conclusive: chỉ hiển thị điều API cho phép.
/// </summary>
public static class ComparisonConclusion
{
    public const string AddLabelsHint =
        "Thêm CV có nhãn vào bộ đánh giá (gợi ý 30–50 CV, có thể dùng “Lấy đề xuất từ quyết định”), rồi chạy lại cả hai phiên bản.";

    /// <summary>
    /// Mốc so sánh = lượt Completed hợp lệ mới nhất của bản đang dùng trên cùng bộ CV (một dòng của bảng B15).
    /// Trả Base/Candidate khi cần gọi API; ngược lại trả khung giải thích vì sao không so sánh.
    /// </summary>
    public static (PromptTestRunDto? Base, PromptTestRunDto? Candidate, ConclusionView? Reason) Pair(
        IReadOnlyList<PromptTestRunDto> rows, PromptTestRunDto? focus, int? activeVersionId, int setId)
    {
        if (focus is null)
            return (null, null, View(ConclusionKind.NoFocus, "Chưa có lượt để kết luận",
                "Bấm “Xem CV lệch” ở một dòng để xem ma trận 2×2 và so lượt đó với bản đang dùng."));
        if (focus.EvaluationSetId != setId || !AccuracyComparisonView.Valid(focus))
            return (null, null, View(ConclusionKind.InvalidFocus, "Chưa so sánh được lượt này",
                $"Lượt #{focus.RunId} không có thống kê hợp lệ trên bộ CV đang so sánh. Hãy làm mới hoặc chọn lượt khác."));
        if (rows.Count < 2)
            return (null, null, View(ConclusionKind.NeedTwoRuns, "Cần ít nhất hai phiên bản để so sánh",
                "Chạy đánh giá thêm một phiên bản khác trên cùng bộ CV để có kết luận so sánh."));
        var active = activeVersionId is { } id ? rows.FirstOrDefault(r => r.PromptVersionId == id) : null;
        if (active is null)
            return (null, null, View(ConclusionKind.NoActiveBase, "Chưa có mốc so sánh",
                activeVersionId is null
                    ? "Chưa có phiên bản đang dùng. Kích hoạt một phiên bản rồi chạy đánh giá để có mốc so sánh."
                    : "Bản đang dùng chưa có lượt hoàn thành hợp lệ trên bộ CV này. Chạy đánh giá bản đang dùng để có mốc so sánh."));
        if (active.RunId == focus.RunId)
            return (null, null, View(ConclusionKind.SameRun, "Đây là lượt của bản đang dùng",
                $"Lượt #{focus.RunId} ({focus.PromptVersionName}) là mốc so sánh. Bấm “Xem CV lệch” ở phiên bản khác để so với bản đang dùng."));
        return (active, focus, null);
    }

    /// <summary>Lượt đã chọn không còn trong bộ CV đang so sánh: không tự nhảy sang lượt khác.</summary>
    public static ConclusionView Missing(int runId) =>
        View(ConclusionKind.InvalidFocus, "Không tìm thấy lượt đã chọn",
            $"Lượt #{runId} không có trong bộ CV đang so sánh. Hãy chọn lại “Xem CV lệch” ở bảng.");

    public static ConclusionView Loading(PromptTestRunDto baseRun, PromptTestRunDto candidate) =>
        View(ConclusionKind.Loading, "Đang so sánh…", $"Đang so {Name(candidate)} với {Name(baseRun)} (bản đang dùng).");

    public static ConclusionView Failed(string? message) =>
        View(ConclusionKind.Error, "Không tải được kết luận so sánh",
            $"{(string.IsNullOrWhiteSpace(message) ? "Gọi API so sánh thất bại." : message.Trim())} Chưa có kết luận nào được đưa ra; hãy thử lại.");

    /// <summary>Đọc PromptCompareDto của đúng cặp (baseRun, candidate) đã yêu cầu.</summary>
    public static ConclusionView Read(PromptCompareDto compare, PromptTestRunDto baseRun, PromptTestRunDto candidate, int setId)
    {
        ArgumentNullException.ThrowIfNull(compare);
        if (compare.Base.RunId != baseRun.RunId || compare.Candidate.RunId != candidate.RunId)
            return View(ConclusionKind.Stale, "Kết quả so sánh không khớp lượt đã chọn",
                "API trả về kết quả của lượt khác. Không hiển thị kết luận; hãy làm mới so sánh.");
        if (compare.Base.EvaluationSetId != setId || compare.Candidate.EvaluationSetId != setId)
            return View(ConclusionKind.NotComparable, "Không so sánh được: khác bộ CV",
                "Hai lượt không cùng bộ CV đánh giá nên không thể kết luận bản nào tốt hơn.");
        if (!AccuracyComparisonView.Matches(baseRun, compare.Base) || !AccuracyComparisonView.Matches(candidate, compare.Candidate))
            return View(ConclusionKind.Stale, "Số liệu vừa thay đổi",
                "Tỷ lệ đúng của một trong hai lượt đã thay đổi (ví dụ vừa sửa nhãn HR). Hãy làm mới so sánh trước khi đọc kết luận.");

        var warnings = compare.Warnings.Where(w => !string.IsNullOrWhiteSpace(w)).Take(3).ToList();
        if (!compare.IsFairComparison || compare.Verdict == "NotComparable")
            return View(ConclusionKind.NotComparable, "Chưa so sánh được hai lượt này",
                FirstText(compare.ConclusionNote, compare.Summary, "Hai lượt khác điều kiện chạy hoặc khác danh sách CV/nhãn HR."), warnings);

        var hasBase = AccuracyText.HasRange(compare.Base.AccuracyPercent, compare.Base.AccuracyLow, compare.Base.AccuracyHigh);
        var hasCandidate = AccuracyText.HasRange(compare.Candidate.AccuracyPercent, compare.Candidate.AccuracyLow, compare.Candidate.AccuracyHigh);
        if (compare.Conclusive)
        {
            if (compare.Verdict is not ("Better" or "Worse"))
                return View(ConclusionKind.NotComparable, "Kết quả so sánh chưa nhất quán",
                    "API báo đủ dữ liệu nhưng không cho biết bản nào cao hơn. Không đưa ra kết luận; hãy làm mới so sánh.", warnings);
            if (!hasBase || !hasCandidate)
                return View(ConclusionKind.MissingInterval, "Thiếu khoảng tin cậy",
                    "Một trong hai lượt chưa có khoảng tin cậy 95% hợp lệ nên chưa thể kết luận. Hãy làm mới hoặc chạy lại đánh giá.", warnings);
            if (AccuracyText.Overlap(compare.Base.AccuracyLow, compare.Base.AccuracyHigh, compare.Candidate.AccuracyLow, compare.Candidate.AccuracyHigh)
                || (compare.Verdict == "Better" && compare.Candidate.AccuracyLow <= compare.Base.AccuracyHigh)
                || (compare.Verdict == "Worse" && compare.Candidate.AccuracyHigh >= compare.Base.AccuracyLow))
                return View(ConclusionKind.Stale, "Kết luận và khoảng tin cậy không nhất quán",
                    "API báo đủ dữ liệu nhưng khoảng tin cậy chưa xác nhận hướng kết luận. Hãy làm mới so sánh.", warnings);
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(compare.ConclusionNote)) notes.Add(compare.ConclusionNote.Trim());
            notes.AddRange(warnings);
            return View(ConclusionKind.Conclusive, "Đủ dữ liệu để kết luận trên bộ CV này",
                FirstText(compare.Summary, null, $"{Name(candidate)} và {Name(baseRun)} khác nhau rõ trên bộ CV này."), notes);
        }

        var message = !hasBase || !hasCandidate
            ? "Một trong hai lượt chưa có khoảng tin cậy 95% nên chưa thể kết luận."
            : AccuracyText.Overlap(compare.Base.AccuracyLow, compare.Base.AccuracyHigh, compare.Candidate.AccuracyLow, compare.Candidate.AccuracyHigh)
                ? $"Khoảng tin cậy 95% của {Name(baseRun)} là {AccuracyText.WithRange(compare.Base.AccuracyPercent, compare.Base.AccuracyLow, compare.Base.AccuracyHigh)}, "
                  + $"của {Name(candidate)} là {AccuracyText.WithRange(compare.Candidate.AccuracyPercent, compare.Candidate.AccuracyLow, compare.Candidate.AccuracyHigh)}: "
                  + "hai khoảng chồng lên nhau nên chênh lệch quan sát có thể chỉ do ngẫu nhiên."
                : "Chênh lệch chưa đủ cơ sở để kết luận trên bộ CV này.";
        var hints = new List<string>();
        if (!string.IsNullOrWhiteSpace(compare.Summary)) hints.Add($"Kết quả quan sát (chưa phải kết luận): {compare.Summary.Trim()}");
        hints.Add(AddLabelsHint);
        hints.AddRange(warnings);
        return View(ConclusionKind.Inconclusive, AccuracyText.Inconclusive, message, hints);
    }

    public static string Name(PromptTestRunDto run) => $"{run.PromptVersionName} (lượt #{run.RunId})";

    private static string FirstText(string? first, string? second, string fallback) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;

    private static ConclusionView View(ConclusionKind kind, string title, string message, IReadOnlyList<string>? notes = null) =>
        new(kind, title, message, notes ?? []);
}
