using System.Globalization;
using System.Text;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

/// <summary>B7: nút lọc nhanh của bảng xếp hạng.</summary>
public enum RankingFilter { All, Pass, NotPass, Unscored }

/// <summary>B7: các kiểu sắp xếp. Mặc định: điểm cuối giảm dần.</summary>
public enum RankingSort { FinalDesc, FinalAsc, AiDesc, KeywordDesc, ExperienceDesc, NameAsc }

/// <summary>Số liệu tóm tắt một job trong lượt chấm (các thẻ phía trên bảng).</summary>
public sealed record RankingSummary(int Total, int Scored, int Unscored, int PassCount, decimal? Average, RankingItemDto? Top);

/// <summary>
/// B7: logic thuần của trang Kết quả & xếp hạng (không phụ thuộc giao diện, có unit test):
/// đọc runId/jobId trên địa chỉ, tìm kiếm không dấu, lọc nhanh, sắp xếp, định dạng số và giờ.
/// Quy tắc chung: CV CHƯA CÓ ĐIỂM (đang chờ chấm, lỗi) luôn nằm cuối, hiển thị "—", không bao giờ thành điểm 0.
/// </summary>
public static class RankingView
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static readonly IReadOnlyList<(RankingFilter Value, string Label)> Filters =
    [
        (RankingFilter.All, "Tất cả"),
        (RankingFilter.Pass, "Dự đoán Đạt"),
        (RankingFilter.NotPass, "Không đạt"),
        (RankingFilter.Unscored, "Chưa có điểm")
    ];

    public static readonly IReadOnlyList<(RankingSort Value, string Label)> Sorts =
    [
        (RankingSort.FinalDesc, "Điểm cuối: cao → thấp"),
        (RankingSort.FinalAsc, "Điểm cuối: thấp → cao"),
        (RankingSort.AiDesc, "Điểm AI: cao → thấp"),
        (RankingSort.KeywordDesc, "Điểm keyword: cao → thấp"),
        (RankingSort.ExperienceDesc, "Kinh nghiệm: nhiều → ít"),
        (RankingSort.NameAsc, "Tên ứng viên: A → Z")
    ];

    // ---------------- Tham số trên địa chỉ ----------------

    /// <summary>
    /// Đọc runId/jobId từ địa chỉ. Không có giá trị → (true, null). Có nhưng không phải số nguyên dương
    /// (vd "abc", "0", "-1") → false để trang báo "không hợp lệ" thay vì gọi API với giá trị sai.
    /// </summary>
    public static bool TryParseId(string? raw, out int? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            id = value;
            return true;
        }
        return false;
    }

    public static string ResultsUrl(int runId, int? jobId = null) =>
        jobId is null ? $"results?runId={runId}" : $"results?runId={runId}&jobId={jobId}";

    // ---------------- Tìm kiếm không phân biệt hoa thường, không dấu ----------------

    /// <summary>"Trần Thị Bình" → "tran thi binh"; "Đặng" → "dang". Dùng cho cả từ khóa và dữ liệu.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch { 'đ' => 'd', 'Đ' => 'd', _ => char.ToLowerInvariant(c) });
        }
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Khớp khi MỌI từ trong ô tìm có trong tên ứng viên hoặc tên file ("binh tran" vẫn tìm ra "Trần Thị Bình").</summary>
    public static bool MatchesSearch(RankingItemDto item, string? query)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var haystack = Fold(item.CandidateName) + " " + Fold(item.FileName);
        return words.All(haystack.Contains);
    }

    // ---------------- Lọc nhanh và sắp xếp ----------------

    /// <summary>Đã có điểm: chấm xong và có điểm cuối (cùng quy tắc xếp hạng của A13).</summary>
    public static bool IsScored(RankingItemDto item) =>
        item.Status == EvaluationStatuses.Completed && item.FinalScore is not null;

    public static bool MatchesFilter(RankingItemDto item, RankingFilter filter) => filter switch
    {
        RankingFilter.Pass => IsScored(item) && item.PredictedPass == true,
        RankingFilter.NotPass => IsScored(item) && item.PredictedPass != true,
        RankingFilter.Unscored => !IsScored(item),
        _ => true
    };

    /// <summary>Số trên mỗi nút lọc = số dòng hiện ra khi bấm nút đó (đã tính cả ô tìm kiếm).</summary>
    public static int Count(IEnumerable<RankingItemDto> items, string? query, RankingFilter filter) =>
        items.Count(i => MatchesSearch(i, query) && MatchesFilter(i, filter));

    public static List<RankingItemDto> Apply(IEnumerable<RankingItemDto> items, string? query, RankingFilter filter, RankingSort sort) =>
        Sort(items.Where(i => MatchesSearch(i, query) && MatchesFilter(i, filter)), sort).ToList();

    /// <summary>
    /// Sắp xếp ổn định. Với mọi kiểu sắp xếp: CV đã có điểm trước, CV chưa có điểm cuối (theo tên file).
    /// Trong cùng nhóm, giá trị thiếu (vd CV "chỉ keyword" không có điểm AI) xếp sau giá trị có thật.
    /// </summary>
    public static IEnumerable<RankingItemDto> Sort(IEnumerable<RankingItemDto> items, RankingSort sort)
    {
        var scoredFirst = items.OrderBy(i => IsScored(i) ? 0 : 1);
        var ordered = sort switch
        {
            RankingSort.FinalAsc => scoredFirst.ThenBy(i => i.FinalScore is null).ThenBy(i => i.FinalScore),
            RankingSort.AiDesc => scoredFirst.ThenBy(i => i.AiScore is null).ThenByDescending(i => i.AiScore)
                .ThenByDescending(i => i.FinalScore),
            RankingSort.KeywordDesc => scoredFirst.ThenBy(i => i.KeywordScore is null).ThenByDescending(i => i.KeywordScore)
                .ThenByDescending(i => i.FinalScore),
            RankingSort.ExperienceDesc => scoredFirst.ThenBy(i => i.YearsExperience is null).ThenByDescending(i => i.YearsExperience)
                .ThenByDescending(i => i.FinalScore),
            RankingSort.NameAsc => scoredFirst.ThenBy(i => Fold(i.CandidateName ?? i.FileName), StringComparer.Ordinal),
            _ => scoredFirst.ThenBy(i => i.FinalScore is null).ThenByDescending(i => i.FinalScore)
        };
        return ordered.ThenBy(i => i.Rank ?? int.MaxValue).ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.EvaluationId);
    }

    public static RankingSummary Summarize(RankingResponse ranking)
    {
        var scored = ranking.Items.Where(IsScored).ToList();
        return new RankingSummary(
            ranking.Items.Count,
            scored.Count,
            ranking.Items.Count - scored.Count,
            ranking.PassCount,
            scored.Count == 0 ? null : Math.Round(scored.Average(i => i.FinalScore!.Value), 1),
            scored.OrderByDescending(i => i.FinalScore).ThenBy(i => i.Rank ?? int.MaxValue).FirstOrDefault());
    }

    // ---------------- Hiển thị ----------------

    /// <summary>91,6 · 70 · "—" khi chưa có điểm (không bao giờ hiện 0 giả).</summary>
    public static string Score(decimal? value) => value is null ? "—" : value.Value.ToString("0.#", Vi);

    public static string Years(decimal? value) => value is null ? "—" : value.Value.ToString("0.#", Vi) + " năm";

    public static string RankText(int? rank) => rank is null ? "—" : rank.Value.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Database lưu UTC; giao diện hiển thị giờ Việt Nam (UTC+7, không đổi giờ theo mùa).</summary>
    public static string VnTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(7).ToString("dd/MM/yyyy HH:mm", Vi);

    public static string RunOption(RunSummaryDto run) =>
        $"#{run.RunId} · {VnTime(run.CreatedAt)} · {StatusText.ForRun(run.Status).Text}";

    public static string JobOption(JobCountDto job) => $"{job.Title} ({job.Count} CV)";

    /// <summary>Chữ viết tắt cho ảnh đại diện: "Nguyễn Văn An" → "VA"; chưa rõ tên → 2 ký tự đầu tên file.</summary>
    public static string Initials(string? name, string fileName)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) return string.Concat(parts[^2][..1], parts[^1][..1]).ToUpper(Vi);
        if (parts.Length == 1) return parts[0][..1].ToUpper(Vi);
        var letters = new string(Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Where(char.IsLetterOrDigit).Take(2).ToArray());
        return letters.Length == 0 ? "CV" : letters.ToUpper(Vi);
    }
}
