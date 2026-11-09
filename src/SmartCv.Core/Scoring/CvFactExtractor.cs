using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartCv.Core.Scoring;

/// <summary>
/// A9: đọc GPA và số năm kinh nghiệm từ chữ CV (đã qua <see cref="TextNormalizer"/>: thường, không dấu).
/// Không đọc được thì trả NULL ("không biết"), KHÔNG trả 0: bước chấm sẽ bỏ qua tiêu chí đó thay vì phạt.
/// </summary>
public static partial class CvFactExtractor
{
    private const decimal MaxYears = 40m;
    private const int MaxRangeMonths = 480;

    private enum Section { None, Work, Education, Other }

    /// <summary>
    /// GPA quy về thang 4. Hiểu "GPA 3.2", "GPA: 3,20/4.0", "điểm trung bình 8.1/10", "CPA (thang 4): 3.1".
    /// Thang khác 4 và 10 (ví dụ "/5") không có quy tắc quy đổi nên bỏ qua. Lấy giá trị hợp lệ đầu tiên.
    /// </summary>
    public static decimal? ExtractGpa(string normalizedText)
    {
        foreach (Match m in GpaRegex().Matches(normalizedText))
        {
            if (!TryParse(m.Groups["v"].Value, out var value)) continue;

            decimal? scale = null;
            var scaleText = m.Groups["s"].Success ? m.Groups["s"].Value : m.Groups["s0"].Value;
            if (scaleText.Length > 0)
            {
                if (!TryParse(scaleText, out var parsedScale) || parsedScale is not (4m or 10m)) continue;
                scale = parsedScale;
            }

            if (value > (scale ?? 10m)) continue;
            if (scale == 10m || (scale is null && value > 4m))
                value = Math.Round(value / 10m * 4m, 2, MidpointRounding.AwayFromZero);   // thang 10 → thang 4

            if (value is > 0m and <= 4m) return value;
        }
        return null;
    }

    /// <summary>
    /// Số năm kinh nghiệm làm việc. Lấy giá trị LỚN NHẤT giữa:
    ///   (a) thời lượng ghi thẳng: "1.5 năm", "6 tháng", "2+ years", "1 năm 6 tháng"
    ///   (b) tổng các khoảng thời gian làm việc, phần chồng lấn chỉ tính một lần: "06/2024 - nay", "2020 - 2022"
    /// Bỏ qua mục Học vấn, câu về học tập / khóa học, mục tiêu tương lai và địa chỉ.
    /// Trả null nếu không thấy gì.
    /// </summary>
    public static decimal? ExtractYearsOfExperience(string normalizedText, DateOnly today)
    {
        var explicitYears = 0m;
        var ranges = new List<(int Start, int End)>();
        var todayIndex = MonthIndex(today.Year, today.Month);

        var lines = normalizedText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var section = Section.None;

        for (var i = 0; i < lines.Length; i++)
        {
            if (HeadingOf(lines[i]) is { } heading)
            {
                section = heading;
                continue;
            }
            if (section == Section.Education) continue;

            // Tách theo CÂU: CV hay viết "GPA 3.20. 1.5 năm làm Backend" trên cùng một dòng
            foreach (var sentence in SentenceSplit().Split(lines[i]))
            {
                if (NotWorkSentence().IsMatch(sentence)) continue;

                // Mục khác (tóm tắt, kỹ năng, dự án...): chỉ tin câu nói rõ về kinh nghiệm, ví dụ "Hơn 2 năm kinh nghiệm"
                if (section != Section.Other || ExperienceWord().IsMatch(sentence))
                    explicitYears = Math.Max(explicitYears, LargestDuration(sentence));

                // Khoảng thời gian trong mục khác thường là dự án học tập, hoạt động: không phải thời gian đi làm
                if (section == Section.Other || IsSchoolDateLine(lines, i, sentence)) continue;

                foreach (Match m in RangeRegex().Matches(sentence))
                    if (ToMonthRange(m, todayIndex) is { } range) ranges.Add(range);
            }
        }

        var best = Math.Max(explicitYears, Math.Min(CountDistinctMonths(ranges) / 12m, MaxYears));
        return best > 0 ? Math.Round(best, 1, MidpointRounding.AwayFromZero) : null;
    }

    private static decimal LargestDuration(string sentence)
    {
        var largest = 0m;
        foreach (Match m in DurationRegex().Matches(sentence))
        {
            if (!TryParse(m.Groups["n"].Value, out var n)) continue;

            var isMonths = m.Groups["u"].Value.StartsWith("thang", StringComparison.Ordinal)
                           || m.Groups["u"].Value.StartsWith("month", StringComparison.Ordinal);
            var years = isMonths ? n / 12m : n;
            if (!isMonths && TryParse(m.Groups["mo"].Value, out var extraMonths)) years += extraMonths / 12m;   // "1 năm 6 tháng"

            if (years is > 0 and <= MaxYears) largest = Math.Max(largest, years);
        }
        return largest;
    }

    private static (int Start, int End)? ToMonthRange(Match m, int todayIndex)
    {
        var start = ToMonthIndex(m.Groups["m1"].Value, m.Groups["n1"].Value, m.Groups["y1"].Value, isEnd: false);
        var end = m.Groups["y2"].Success
            ? ToMonthIndex(m.Groups["m2"].Value, m.Groups["n2"].Value, m.Groups["y2"].Value, isEnd: true)
            : todayIndex;   // "nay", "hiện tại", "present"

        if (start is null || end is null || start > todayIndex || end < start) return null;

        var clampedEnd = Math.Min(end.Value, todayIndex);   // mốc kết thúc trong tương lai: chỉ tính đến hôm nay
        return clampedEnd - start.Value + 1 > MaxRangeMonths ? null : (start.Value, clampedEnd);
    }

    /// <summary>Đếm số tháng phân biệt: các khoảng chồng lấn (làm song song 2 nơi) chỉ tính một lần.</summary>
    private static int CountDistinctMonths(List<(int Start, int End)> ranges)
    {
        var total = 0;
        var coveredUntil = int.MinValue;
        foreach (var (start, end) in ranges.OrderBy(r => r.Start))
        {
            var from = Math.Max(start, coveredUntil + 1);
            if (end < from) continue;
            total += end - from + 1;
            coveredUntil = end;
        }
        return total;
    }

    /// <summary>Dòng chỉ có mốc thời gian, nằm sát tên trường: "Đại học A" rồi xuống dòng "2018 - 2022".</summary>
    private static bool IsSchoolDateLine(string[] lines, int index, string sentence)
    {
        if (!IsDateOnly(sentence)) return false;

        var previous = index > 0 ? lines[index - 1] : null;
        if (previous is not null && NotWorkSentence().IsMatch(previous)) return true;

        // Mốc thời gian viết TRƯỚC tên trường: chỉ xét dòng sau khi dòng trước không phải tên công việc
        var previousIsNeutral = previous is null || HeadingOf(previous) is not null || IsDateOnly(previous);
        return previousIsNeutral && index + 1 < lines.Length && NotWorkSentence().IsMatch(lines[index + 1]);
    }

    private static bool IsDateOnly(string text) =>
        RangeRegex().IsMatch(text) && !RangeRegex().Replace(text, string.Empty).Any(char.IsLetter);

    private static Section? HeadingOf(string line)
    {
        var m = HeadingRegex().Match(line);
        if (!m.Success) return null;
        if (m.Groups["w"].Success) return Section.Work;
        return m.Groups["e"].Success ? Section.Education : Section.Other;
    }

    private static int MonthIndex(int year, int month) => year * 12 + month - 1;

    /// <summary>Thiếu tháng thì lấy đầu năm / cuối năm. Tháng sai (13/2024) thì cả mốc không hợp lệ.</summary>
    private static int? ToMonthIndex(string month, string monthName, string year, bool isEnd)
    {
        if (!int.TryParse(year, out var y) || y < 1980) return null;

        int m;
        if (month.Length > 0)
        {
            if (!int.TryParse(month, out m) || m is < 1 or > 12) return null;
        }
        else if (monthName.Length > 0)
        {
            m = Array.IndexOf(MonthNames, monthName) + 1;
        }
        else
        {
            m = isEnd ? 12 : 1;
        }
        return MonthIndex(y, m);
    }

    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static bool TryParse(string s, out decimal value) =>
        decimal.TryParse(s.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    // "gpa 3.2", "gpa: 3,20/4.0", "cpa (thang 4): 3.1", "diem trung binh tich luy 8.1/10", "dtb 7.5", "gpa dat 3.2"
    [GeneratedRegex(@"(?<![a-z])(?:cumulative\s+)?(?:gpa|cpa|diem\s+trung\s+binh(?:\s+chung)?(?:\s+tich\s+luy)?|diem\s+tb(?:\s+tich\s+luy)?|dtb(?:tl)?|grade\s+point\s+average)(?:\s*/\s*(?:gpa|cpa))?(?![a-z])(?:\s*\(?\s*(?:he|thang)(?:\s+diem)?\s*(?<s0>4|10)\s*\)?)?\s*(?:[:=\-]|dat|la|of)?\s*(?<v>\d+(?:[.,]\d+)?)(?:\s*/\s*(?<s>\d+(?:[.,]\d+)?))?")]
    private static partial Regex GpaRegex();

    // "1.5 nam", "2+ nam", "6 thang", "3 years", "18 months", "1 nam 6 thang" (số đứng TRƯỚC đơn vị).
    // Không phải thời lượng: "thang 5 nam 2024", "ngay 3 thang 5", "quan 7 nam dinh", "12 nam ky khoi nghia".
    [GeneratedRegex(@"(?<![\d.,/])(?<!(?:thang|ngay|khoa|lop|quan|phuong|so)\s+)(?<n>\d{1,2}(?:[.,]\d{1,2})?)\s*\+?\s*(?<u>nam|years?|yrs?|thang|months?)(?![a-z])(?!\s*(?:19|20)\d{2})(?!\s+(?:dinh|ky|tu\s+liem|trung\s+bo|phi|sach)(?![a-z]))(?:\s*(?:va|and|,)?\s*(?<mo>\d{1,2})\s*(?:thang|months?)(?![a-z]))?")]
    private static partial Regex DurationRegex();

    // "06/2024 - nay", "2020 - 2022", "3/2023 - 12/2024", "t6/2023 den hien tai", "thang 6 nam 2023 - nay", "jun 2023 to present"
    [GeneratedRegex(@"(?<![\p{L}\d])(?:(?:(?:t|thang\s*)?(?<m1>\d{1,2})\s*(?:[/.\-]|nam)\s*|(?<n1>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s*,?\s*))?(?<y1>(?:19|20)\d{2})(?!\d)\s*(?:-|~|>|→|den|toi|to|until)\s*(?:(?:(?:(?:t|thang\s*)?(?<m2>\d{1,2})\s*(?:[/.\-]|nam)\s*|(?<n2>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s*,?\s*))?(?<y2>(?:19|20)\d{2})(?!\d)|(?:nay|hien\s+tai|hien\s+nay|present|now|current|today|dang\s+lam)(?![a-z]))")]
    private static partial Regex RangeRegex();

    // Ranh giới câu: chấm phẩy, gạch đầu dòng, "|", dấu chấm + khoảng trắng (không cắt "1.5", "jun. 2023")
    [GeneratedRegex(@";|•|\||(?<!(?<![a-z])(?:jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec))\.\s+")]
    private static partial Regex SentenceSplit();

    // Tiêu đề mục: chiếm trọn một dòng
    [GeneratedRegex(@"^[^a-z0-9]*(?:(?<w>kinh\s+nghiem(?:\s+(?:lam\s+viec|thuc\s+te|chuyen\s+mon))?|(?:work(?:ing)?\s+|professional\s+)?experience|employment(?:\s+history)?|qua\s+trinh\s+(?:cong\s+tac|lam\s+viec)|lich\s+su\s+(?:cong\s+tac|lam\s+viec))|(?<e>(?:trinh\s+do\s+)?hoc\s+van|qua\s+trinh\s+hoc\s+tap|education|academic\s+background|bang\s+cap|dao\s+tao)|(?<o>ky\s+nang(?:\s+(?:chuyen\s+mon|mem|lap\s+trinh))?|(?:technical\s+|soft\s+)?skills?|chung\s+chi|certifications?|certificates?|du\s+an(?:\s+(?:ca\s+nhan|da\s+thuc\s+hien|tieu\s+bieu))?|(?:personal\s+)?projects?|hoat\s+dong(?:\s+ngoai\s+khoa)?|(?:extracurricular\s+)?activities|muc\s+tieu(?:\s+nghe\s+nghiep)?|(?:career\s+)?objective|summary|gioi\s+thieu(?:\s+ban\s+than)?|thong\s+tin\s+ca\s+nhan|personal\s+information|contact|lien\s+he|so\s+thich|interests|giai\s+thuong|awards?|ngoai\s+ngu|languages?|tham\s+chieu|references?))[^a-z0-9]*$")]
    private static partial Regex HeadingRegex();

    // Câu KHÔNG nói về thời gian đi làm: học tập, khóa học, mục tiêu tương lai, địa chỉ / liên hệ
    [GeneratedRegex(@"\b(?:dai\s+hoc|hoc\s+vien|cao\s+dang|university|college|thpt|gpa|cpa|chuyen\s+nganh|nganh\s+hoc|khoa\s+hoc|sinh\s+vien|hoc\s+ky|nam\s+hoc|tot\s+nghiep|hoc\s+van|education|cu\s+nhan|thac\s+si|tien\s+si|bachelor|master\s+of|do\s+an|courses?|bootcamp|training|dao\s+tao|tu\s+hoc|self[\s-]?stud\w*|muc\s+tieu|cam\s+ket|mong\s+muon|dinh\s+huong|objective|goals?|aim\s+to|in\s+the\s+next|nam\s+toi|bao\s+hanh|warranty|dia\s+chi|address|email|dien\s+thoai|phone)\b")]
    private static partial Regex NotWorkSentence();

    [GeneratedRegex(@"\b(?:kinh\s+nghiem|experience|lam\s+viec|working)\b")]
    private static partial Regex ExperienceWord();
}
