using System.Text.RegularExpressions;
using SmartCv.Core.Options;

namespace SmartCv.Core.Scoring;

/// <summary>
/// A9: chấm điểm keyword trên chữ CV đã trích xuất ở A7 (Cvs.ExtractedText). Không đọc file, không OCR, không gọi AI.
///
/// Điểm (0–100) = trung bình có trọng số của các tiêu chí ÁP DỤNG ĐƯỢC:
///   Kỹ năng bắt buộc  60%  = số kỹ năng bắt buộc tìm thấy / tổng số
///   Kỹ năng ưu tiên   20%  = số kỹ năng ưu tiên tìm thấy / tổng số
///   GPA               10%  = GPA ≥ yêu cầu → đủ điểm; thấp hơn → tỷ lệ GPA / yêu cầu
///   Kinh nghiệm       10%  = số năm ≥ yêu cầu → đủ điểm; thấp hơn → tỷ lệ
/// Tiêu chí KHÔNG áp dụng (job không yêu cầu, hoặc CV không ghi) thì bỏ ra và chia lại trọng số cho tiêu chí còn lại.
/// Điểm này chiếm 30% điểm cuối; việc gộp với 70% điểm AI thuộc A10.
///
/// Hàm thuần: không database, không mạng.
/// </summary>
public static class KeywordScoringService
{
    private const int EvidenceBefore = 40;
    private const int EvidenceAfter = 80;

    private const string PendingApprovalSuffix = "(quy tắc đang chờ phê duyệt nghiệp vụ)";

    /// <exception cref="ArgumentException">CV chưa có chữ. Người gọi phải kiểm tra trước: CV rỗng không được chấm thành 0 điểm.</exception>
    public static KeywordScoreResult Score(JobProfile job, string? cvText, ScoringOptions options, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(cvText))
            throw new ArgumentException("CV chưa có nội dung nên không chấm được.", nameof(cvText));

        var original = TextNormalizer.Compose(cvText);
        var normalized = TextNormalizer.Normalize(original);
        var notes = new List<string>();

        // ---- 1. Kỹ năng ----
        var skills = job.Skills.Select(s => MatchSkill(s, original, normalized)).ToList();

        var must = skills.Where(s => s.IsMustHave).ToList();
        var nice = skills.Where(s => !s.IsMustHave).ToList();

        var parts = new List<(decimal Weight, decimal Ratio)>();
        if (must.Count > 0)
            parts.Add((options.KeywordMustHaveWeight, (decimal)must.Count(s => s.IsMatched) / must.Count));
        if (nice.Count > 0)
            parts.Add((options.KeywordNiceToHaveWeight, (decimal)nice.Count(s => s.IsMatched) / nice.Count));

        // ---- 2. GPA ----
        var gpa = CvFactExtractor.ExtractGpa(normalized);
        if (job.MinGpa is not { } minGpa || minGpa <= 0)
            notes.Add("Vị trí không yêu cầu GPA.");
        else if (gpa is null)
            notes.Add($"CV không ghi GPA: bỏ qua tiêu chí GPA và chia lại trọng số {PendingApprovalSuffix}.");
        else
            parts.Add((options.KeywordGpaWeight, Math.Min(1m, gpa.Value / minGpa)));

        // ---- 3. Kinh nghiệm ----
        var years = CvFactExtractor.ExtractYearsOfExperience(normalized, today);
        if (job.MinYearsExperience <= 0)
            notes.Add("Vị trí không yêu cầu số năm kinh nghiệm.");
        else if (years is null)
            notes.Add($"CV không ghi rõ thời gian kinh nghiệm: bỏ qua tiêu chí này và chia lại trọng số {PendingApprovalSuffix}.");
        else
            parts.Add((options.KeywordExperienceWeight, Math.Min(1m, years.Value / job.MinYearsExperience)));

        // ---- 4. Gộp: chia lại trọng số cho các tiêu chí áp dụng được ----
        var totalWeight = parts.Sum(p => p.Weight);
        var hasCriteria = totalWeight > 0;
        var score = hasCriteria
            ? Math.Round(parts.Sum(p => p.Weight * p.Ratio) / totalWeight * 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;
        if (!hasCriteria) notes.Add("Không có tiêu chí nào áp dụng được nên không tính được điểm keyword.");

        var missing = must.Where(s => !s.IsMatched).Select(s => s.DisplayName).ToList();
        if (missing.Count > 0) notes.Add($"Thiếu kỹ năng bắt buộc: {string.Join(", ", missing)}.");

        return new KeywordScoreResult(Math.Clamp(score, 0m, 100m), skills, gpa, years, notes, hasCriteria);
    }

    /// <summary>Tìm một kỹ năng theo tên hiển thị, mã kỹ năng và các từ khóa tương đương (JobSkillAliases).</summary>
    private static SkillMatch MatchSkill(SkillProfile skill, string original, string normalized)
    {
        var terms = new[] { skill.DisplayName, skill.SkillCode.Replace('_', ' ') }
            .Concat(skill.Aliases)
            .Select(t => TextNormalizer.Normalize(TextNormalizer.Compose(t)).Trim())
            .Where(t => t.Length > 0)
            .Distinct()
            .OrderByDescending(t => t.Length)   // ưu tiên cụm dài: "sql server" trước "sql"
            .ThenBy(t => t, StringComparer.Ordinal);

        foreach (var term in terms)
        {
            if (TermRegex(term) is not { } regex) continue;
            var m = regex.Match(normalized);
            if (m.Success)
                return new SkillMatch(skill.SkillCode, skill.DisplayName, skill.IsMustHave, true, Evidence(original, m.Index, m.Length));
        }
        return new SkillMatch(skill.SkillCode, skill.DisplayName, skill.IsMustHave, false, null);
    }

    /// <summary>
    /// So khớp NGUYÊN TỪ: "java" không khớp "javascript", "sql" không khớp "mysql", "c" không khớp "c#" / "c++".
    /// Khoảng trắng, gạch nối trong cụm từ khớp với khoảng trắng / xuống dòng / gạch nối: "machine learning" ~ "machine-learning".
    /// Từ khóa được Regex.Escape và không có lượng từ lồng nhau, nên thời gian khớp tuyến tính theo độ dài CV.
    /// </summary>
    private static Regex? TermRegex(string normalizedTerm)
    {
        var tokens = normalizedTerm.Split([' ', '\t', '\r', '\n', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;

        var pattern = string.Join(@"[\s\-_]+", tokens.Select(Regex.Escape));
        return new Regex($@"(?<![\p{{L}}\p{{N}}]){pattern}(?![\p{{L}}\p{{N}}#+])", RegexOptions.CultureInvariant);
    }

    /// <summary>Cắt đoạn chữ GỐC (còn dấu) quanh vị trí tìm thấy, không cắt giữa từ hay giữa ký tự emoji.</summary>
    private static string Evidence(string original, int index, int length)
    {
        var matchEnd = index + length;
        var start = Math.Max(0, index - EvidenceBefore);
        var end = Math.Min(original.Length, matchEnd + EvidenceAfter);

        if (start > 0 && !char.IsWhiteSpace(original[start - 1]))
        {
            var nextSpace = FirstWhiteSpace(original, start, index);
            if (nextSpace >= 0) start = nextSpace + 1;
        }
        if (end < original.Length && !char.IsWhiteSpace(original[end]))
        {
            var lastSpace = LastWhiteSpace(original, matchEnd, end);
            if (lastSpace >= 0) end = lastSpace;
        }

        if (start > 0 && start < index && char.IsLowSurrogate(original[start])) start++;
        if (end < original.Length && end > matchEnd && char.IsLowSurrogate(original[end])) end--;

        var snippet = string.Join(' ', original[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (start > 0 ? "…" : "") + snippet + (end < original.Length ? "…" : "");
    }

    private static int FirstWhiteSpace(string text, int from, int until)
    {
        for (var i = from; i < until; i++)
            if (char.IsWhiteSpace(text[i])) return i;
        return -1;
    }

    private static int LastWhiteSpace(string text, int from, int until)
    {
        for (var i = until - 1; i >= from; i--)
            if (char.IsWhiteSpace(text[i])) return i;
        return -1;
    }
}
