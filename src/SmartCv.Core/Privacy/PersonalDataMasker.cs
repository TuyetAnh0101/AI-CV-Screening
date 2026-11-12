using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartCv.Core.Privacy;

/// <summary>Họ tên dùng để che lấy từ đâu (để ghi log và kiểm thử; không chứa nội dung).</summary>
public enum NameSource
{
    /// <summary>Không xác định được họ tên: chỉ che email, điện thoại, link, ngày sinh, giới tính.</summary>
    None,
    /// <summary>Họ tên do người gọi truyền vào (bảng Candidates).</summary>
    Provided,
    /// <summary>Sau nhãn "Họ và tên:" / "Full name:".</summary>
    Label,
    /// <summary>Đoán từ phần đầu CV.</summary>
    Header,
    /// <summary>Lưới an toàn: chuỗi viết hoa bắt đầu/kết thúc bằng họ Việt phổ biến, khi các cách trên không ra tên.</summary>
    Fallback
}

/// <summary>Kết quả che: nội dung đã che và số chỗ đã thay theo từng loại (để ghi log số lượng, không ghi nội dung).</summary>
public sealed record MaskResult(string Text, int Emails, int Phones, int Links, int Names, int BirthDates, int Genders)
{
    public int Total => Emails + Phones + Links + Names + BirthDates + Genders;

    /// <summary>Nguồn họ tên đã dùng để che.</summary>
    public NameSource NameSource { get; init; }
}

/// <summary>
/// A37: che thông tin cá nhân trong nội dung CV TRƯỚC KHI gửi Gemini (chỉ gửi thông tin phục vụ đánh giá năng lực).
///   email                         → [EMAIL]
///   số điện thoại VN và quốc tế   → [PHONE]   (0912 345 678 · 0912.345.678 · +84 912 345 678 · (+84)912345678 · (028) 3823 4567 · +1 (415) 555-2671)
///   LinkedIn, Facebook, GitHub    → [LINK]    (kể cả trang cá nhân *.github.io)
///   họ tên ứng viên               → [CANDIDATE]
///   ngày / năm sinh sau nhãn      → [DOB]     (giữ nhãn: "Ngày sinh: [DOB]")
///   giới tính sau nhãn            → [GENDER]
/// Họ tên, theo thứ tự ưu tiên:
///   1. tên truyền vào (bảng Candidates);
///   2. sau nhãn "Họ và tên:" / "Full name:";
///   3. phần đầu CV: bỏ qua tiêu đề ("SƠ YẾU LÝ LỊCH") và chức danh ("Backend Developer"), tách "TÊN - Chức danh" / "Tên | Chức danh";
///      dòng đầu tiên được đoán như trước, các dòng sau phải bắt đầu/kết thúc bằng họ Việt phổ biến; dừng ở mục nội dung ("KINH NGHIỆM");
///   4. lưới an toàn khi 1–3 không ra tên: chuỗi 3–5 chữ viết hoa bắt đầu/kết thúc bằng họ Việt (bỏ qua địa danh, tên trường, đường phố).
/// Mỗi tên được che: đúng thứ tự (không phân biệt hoa thường, có dấu hay mất dấu), mọi thứ tự khác của đủ các chữ và cặp họ + tên
/// ("Khoa Trần Minh", "Khoa Tran", "Trần Khoa"); hai dạng sau chỉ che khi các chữ viết hoa chữ cái đầu (không che "thành công" thường).
/// KHÔNG che: chức danh, kỹ năng, năm học "2019 - 2023", tháng "06/2025", GPA "3.2", số tiền "1.500.000", điểm "8.0/10",
/// phiên bản ".NET 8", tên công nghệ "GitHub Actions", "SQL Server 2022".
/// Hàm thuần, không ghi log: người gọi chỉ được ghi các con số trong <see cref="MaskResult"/>.
/// Keyword scoring (A9) và database vẫn dùng nội dung GỐC; chỉ phần gửi AI được che.
/// </summary>
public static partial class PersonalDataMasker
{
    public const string EmailToken = "[EMAIL]";
    public const string PhoneToken = "[PHONE]";
    public const string LinkToken = "[LINK]";
    public const string NameToken = "[CANDIDATE]";
    public const string BirthDateToken = "[DOB]";
    public const string GenderToken = "[GENDER]";

    private const int MaxNameWords = 5;
    private const int MaxNameLineLength = 40;
    private const int HeaderLines = 8;
    private const int MinFallbackNameWords = 3;

    // email: phần trước @ cho phép chữ, số, . _ % + -
    [GeneratedRegex(@"(?<![\w.+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}(?![\w])")]
    private static partial Regex EmailRegex();

    // mạng xã hội cá nhân, có hoặc không có https://, www.; trang cá nhân <tên>.github.io.
    // Không đứng sau chữ/số/dấu gạch: "mygithub.com" không bị cắt thành "my[LINK]".
    [GeneratedRegex(@"(?<![\w-])(?:https?://)?(?:(?:www|m|vn|mobile)\.)?(?:linkedin\.com|facebook\.com|fb\.com|fb\.me|github\.com|[A-Za-z0-9-]+\.github\.io)(?:/[^\s,;()<>""'|]*)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    // di động VN: (+84 | 84 | (+84) | 0) + đầu số 3/5/7/8/9 + 8 chữ số; giữa các chữ số có thể có 1 dấu cách / chấm / gạch
    [GeneratedRegex(@"(?<![\d+])(?:\(?\+?84\)?[\s.\-]?|0)[35789](?:[\s.\-]?\d){8}(?!\d)")]
    private static partial Regex VnMobileRegex();

    // cố định VN: (+84 | 0) + 2x + 8 chữ số, mã vùng có thể trong ngoặc: (028) 3823 4567
    [GeneratedRegex(@"(?<![\d+])(?:\(?\+?84\)?[\s.\-]?|\(?0)2\d\)?(?:[\s.\-]?\d){8}(?!\d)")]
    private static partial Regex VnLandlineRegex();

    // quốc tế (không phải +84): + mã nước 1–3 số, sau đó các nhóm số; tổng 8–15 chữ số mới coi là điện thoại
    [GeneratedRegex(@"(?<![\w+])\+(?!84)\d{1,3}(?:[\s.\-]?\(?\d{1,5}\)?){2,6}(?!\d)")]
    private static partial Regex IntlPhoneRegex();

    // "Ngày sinh: 12/03/2002", "Sinh năm 2002", "Ngày sinh: 05 tháng 3 năm 2001", "DOB 2002-03-12" → giữ nhãn, che giá trị
    [GeneratedRegex(@"(?<![\p{L}])(?<label>(?:ngày\s*sinh|ngay\s*sinh|năm\s*sinh|nam\s*sinh|sinh\s*ngày|sinh\s*ngay|sinh\s*năm|sinh\s*nam|date\s*of\s*birth|dob|birthday|born(?:\s+(?:on|in))?)\s*[:\-]?\s*)(?<value>\d{1,2}\s*th[aá]ng\s*\d{1,2}(?:\s*(?:n[aă]m|,)\s*\d{4})?|\d{1,4}(?:\s*[/.\-]\s*\d{1,4}){0,2})(?!\d)",
        RegexOptions.IgnoreCase)]
    private static partial Regex BirthDateRegex();

    // "Giới tính: Nam / Nữ / Male / Female"
    [GeneratedRegex(@"(?<![\p{L}])(?<label>(?:giới\s*tính|gioi\s*tinh|gender|sex)\s*[:\-]?\s*)(?<value>nam|nữ|nu|male|female|khác|khac|other)(?![\p{L}])",
        RegexOptions.IgnoreCase)]
    private static partial Regex GenderRegex();

    // "Họ và tên: Nguyễn Văn An" / "Full name: ..." ở bất kỳ dòng nào (mẫu CV có bảng thông tin cá nhân)
    [GeneratedRegex(@"(?<![\p{L}])(?:họ\s+và\s+tên|họ\s+tên|ho\s+va\s+ten|ho\s+ten|full\s*name)\s*[:\-–]\s*(?<name>[^\r\n|,;]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex LabeledNameRegex();

    // Phần đầu CV hay ghép "TÊN - Chức danh", "Tên | Chức danh", "Tên • Thành phố", "Tên, Chức danh"
    [GeneratedRegex(@"\s+[-–—]\s+|\s*[|•·;]\s*|,\s*")]
    private static partial Regex HeaderSeparatorRegex();

    // Chuỗi 2+ chữ viết hoa chữ cái đầu (hoặc in hoa toàn bộ) liền nhau trên cùng một dòng: ứng viên cho lưới an toàn
    [GeneratedRegex(@"(?<![\p{L}\p{N}])\p{Lu}\p{L}*(?:[ \t]+\p{Lu}\p{L}*)+(?![\p{L}\p{N}])")]
    private static partial Regex CapitalizedRunRegex();

    public static MaskResult Mask(string? text, string? candidateName = null)
    {
        if (string.IsNullOrEmpty(text)) return new MaskResult(text ?? string.Empty, 0, 0, 0, 0, 0, 0);

        var s = text.Normalize(NormalizationForm.FormC);
        int emails = 0, phones = 0, links = 0, dob = 0, gender = 0;

        // Thứ tự: email và link TRƯỚC (đường dẫn có thể chứa tên và chữ số), rồi ngày sinh (trước điện thoại), rồi điện thoại
        s = EmailRegex().Replace(s, _ => { emails++; return EmailToken; });
        s = LinkRegex().Replace(s, m => { links++; return LinkToken + TrailingPunctuation(m.Value); });
        s = BirthDateRegex().Replace(s, m => { dob++; return m.Groups["label"].Value + BirthDateToken; });
        s = GenderRegex().Replace(s, m => { gender++; return m.Groups["label"].Value + GenderToken; });
        s = VnMobileRegex().Replace(s, _ => { phones++; return PhoneToken; });
        s = VnLandlineRegex().Replace(s, _ => { phones++; return PhoneToken; });
        s = IntlPhoneRegex().Replace(s, m =>
        {
            var digits = m.Value.Count(char.IsDigit);
            if (digits is < 8 or > 15) return m.Value;   // quá ngắn / quá dài: không phải số điện thoại
            phones++;
            return PhoneToken;
        });

        int names;
        NameSource source;
        if (!string.IsNullOrWhiteSpace(candidateName))
        {
            s = MaskName(s, candidateName, out names);
            source = NameSource.Provided;
        }
        else if (GuessCandidateName(s, out source) is { } guessed)
        {
            s = MaskName(s, guessed, out names);
        }
        else
        {
            s = MaskLikelyNames(s, out names);
            source = names > 0 ? NameSource.Fallback : NameSource.None;
        }

        return new MaskResult(s, emails, phones, links, names, dob, gender) { NameSource = source };
    }

    /// <summary>Đoán họ tên khi chưa biết (nhãn họ tên, rồi phần đầu CV). Null khi không đủ tin cậy.</summary>
    public static string? GuessCandidateName(string text) => GuessCandidateName(text, out _);

    private static string? GuessCandidateName(string text, out NameSource source)
    {
        source = NameSource.None;
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (Match m in LabeledNameRegex().Matches(text))
        {
            var labeled = TakeNameWords(m.Groups["name"].Value);
            if (labeled is null) continue;
            source = NameSource.Label;
            return labeled;
        }

        var firstContentLine = true;
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(HeaderLines))
        {
            var heading = Fold(line.Split(':')[0]).Trim(' ', '.');
            if (DocumentHeadings.Contains(heading)) continue;   // "SƠ YẾU LÝ LỊCH", "CURRICULUM VITAE"
            if (SectionHeadings.Contains(heading)) break;       // "KINH NGHIỆM", "HỌC VẤN": hết phần đầu CV

            foreach (var segment in HeaderSeparatorRegex().Split(line))
            {
                var candidate = segment.Trim().TrimEnd('.', ':', '!');
                // Dòng đầu tiên: đoán như trước (2–5 chữ, hoa). Dòng sau (dưới chức danh...): phải có họ Việt ở đầu hoặc cuối.
                if (LooksLikeName(candidate, requireSurname: !firstContentLine))
                {
                    source = NameSource.Header;
                    return string.Join(' ', candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                }
            }
            firstContentLine = false;
        }
        return null;
    }

    private static bool LooksLikeName(string candidate, bool requireSurname)
    {
        if (candidate.Length is 0 or > MaxNameLineLength) return false;
        var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > MaxNameWords) return false;
        if (!words.All(w => w.All(char.IsLetter))) return false;

        var allUpper = candidate == candidate.ToUpperInvariant();
        var titleCase = words.All(w => char.IsUpper(w[0]));
        if (!allUpper && !titleCase) return false;
        if (!IsPlausibleName(words)) return false;
        return !requireSurname || IsSurname(words[0], words.Length) || IsSurname(words[^1], words.Length);
    }

    /// <summary>Không phải chức danh, mục CV, tổ chức, địa danh (dùng chung cho đoán phần đầu và lưới an toàn).</summary>
    private static bool IsPlausibleName(string[] words)
    {
        // "THPT Lê Quý Đôn", "Trường Nguyễn Du": tên tổ chức mang tên người (so sánh có dấu: "Trường" khác họ "Trương")
        var lower = string.Join(' ', words).ToLowerInvariant() + " ";
        if (InstitutionStarts.Any(p => lower.StartsWith(p + " ", StringComparison.Ordinal))) return false;
        var folded = words.Select(Fold).ToArray();
        if (folded.Any(NonNameWords.Contains)) return false;
        var joined = string.Join(' ', folded);
        if (Places.Contains(joined)) return false;
        var padded = " " + joined + " ";
        return !NonNamePhrases.Any(p => padded.Contains($" {p} ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Lưới an toàn khi không xác định được tên: che các chuỗi 3–5 chữ viết hoa bắt đầu (hoặc kết thúc) bằng họ Việt phổ biến,
    /// không đứng sau tên trường, tổ chức, đường phố, địa giới ("Đại học Tôn Đức Thắng", "THPT Lê Quý Đôn", "đường Nguyễn Huệ").
    /// </summary>
    private static string MaskLikelyNames(string text, out int count)
    {
        var found = new List<string>();
        foreach (Match run in CapitalizedRunRegex().Matches(text))
        {
            var words = run.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var lineStart = text.LastIndexOf('\n', run.Index) + 1;
            var before = text[lineStart..run.Index];

            for (var i = 0; i < words.Length; i++)
            {
                var take = 0;
                if (IsSurname(words[i], words.Length - i))
                {
                    // Họ ở đầu: lấy tới khi gặp chữ không phải tên (chức danh, công nghệ...) hoặc đủ 5 chữ
                    while (i + take < words.Length && take < MaxNameWords && !NonNameWords.Contains(Fold(words[i + take]))) take++;
                }
                else if (i == 0 && words.Length is >= MinFallbackNameWords and <= MaxNameWords && IsSurname(words[^1], words.Length))
                {
                    take = words.Length;   // thứ tự phương Tây: "Khoa Minh Tran"
                }
                if (take < MinFallbackNameWords) continue;

                var name = words[i..(i + take)];
                // Chữ đứng trước tên trên cùng dòng (kể cả các chữ viết hoa đứng trước trong cùng chuỗi: "Đại Học Tôn Đức Thắng")
                var context = string.Join(' ', Fold(before + " " + string.Join(' ', words[..i]))
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) + " ";
                if (IsPlausibleName(name) && !InstitutionPrefixes.Any(p => (" " + context).EndsWith($" {p} ", StringComparison.Ordinal)))
                    found.Add(string.Join(' ', name));
                break;
            }
        }

        count = 0;
        foreach (var name in found.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            text = MaskName(text, name, out var n);
            count += n;
        }
        return text;
    }

    /// <summary>Lấy 2–5 chữ đầu sau nhãn tên, dừng ở chữ không phải chữ cái hoặc nhãn trường khác ("Ngày sinh", "Email"...).</summary>
    private static string? TakeNameWords(string value)
    {
        var words = new List<string>();
        foreach (var word in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!word.All(char.IsLetter) || FieldStartWords.Contains(word.ToLowerInvariant()) || words.Count == MaxNameWords) break;
            words.Add(word);
        }
        return words.Count >= 2 ? string.Join(' ', words) : null;
    }

    /// <summary>Dấu câu cuối đường dẫn thường là dấu câu của câu văn: giữ lại sau [LINK].</summary>
    private static string TrailingPunctuation(string value)
    {
        var end = value.Length;
        while (end > 0 && value[end - 1] is '.' or ':' or '!' or '?') end--;
        return value[end..];
    }

    /// <summary>
    /// Che một họ tên (từ 2 chữ). Ba lượt, mỗi lượt trên bản đã che của lượt trước:
    ///   A. đúng thứ tự: không phân biệt hoa thường, có dấu hay mất dấu, khoảng trắng bất kỳ (kể cả xuống dòng do OCR);
    ///   B. đủ các chữ nhưng thứ tự khác ("Khoa Trần Minh", "Khoa Minh Tran");
    ///   C. (tên từ 3 chữ) cặp họ + tên theo hai chiều ("Trần Khoa", "Khoa Tran").
    /// B và C chỉ che khi MỌI chữ viết hoa chữ cái đầu, để không che cụm từ thường ("triển khai thành công").
    /// Không che tên một chữ hay chỉ tên gọi ("Khoa" còn là "khoa" của trường đại học).
    /// </summary>
    private static string MaskName(string text, string candidateName, out int count)
    {
        count = 0;
        var tokens = Fold(candidateName.Normalize(NormalizationForm.FormC))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return text;

        // A. đúng thứ tự
        text = ReplaceFolded(text, string.Join(@"\s+", tokens.Select(Regex.Escape)), _ => true, ref count);

        // B. thứ tự khác: chuỗi đúng N chữ thuộc tên, đúng số lần mỗi chữ
        var alternatives = string.Join('|', tokens.Distinct().OrderByDescending(t => t.Length).Select(Regex.Escape));
        var anyOrder = $"(?:{alternatives})" + string.Concat(Enumerable.Repeat($@"\s+(?:{alternatives})", tokens.Length - 1));
        var expected = tokens.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        text = ReplaceFolded(text, anyOrder, m =>
            m.Folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).OrderBy(t => t, StringComparer.Ordinal).SequenceEqual(expected)
            && m.Capitalized, ref count);

        // C. cặp họ + tên (tên từ 3 chữ)
        if (tokens.Length >= 3)
        {
            var (family, given) = (Regex.Escape(tokens[0]), Regex.Escape(tokens[^1]));
            text = ReplaceFolded(text, $@"(?:{family}\s+{given}|{given}\s+{family})", m => m.Capitalized, ref count);
        }
        return text;
    }

    private readonly record struct NameMatch(string Folded, bool Capitalized);

    /// <summary>Tìm trên bản "gấp" (bỏ dấu, viết thường, cùng độ dài) rồi thay đúng vị trí trên bản gốc.</summary>
    private static string ReplaceFolded(string text, string wordsPattern, Func<NameMatch, bool> accept, ref int count)
    {
        var folded = Fold(text);
        var matches = Regex.Matches(folded, @"(?<![\p{L}\p{N}])" + wordsPattern + @"(?![\p{L}\p{N}])", RegexOptions.CultureInvariant);
        if (matches.Count == 0) return text;

        var sb = new StringBuilder(text);
        for (var i = matches.Count - 1; i >= 0; i--)   // thay từ cuối lên để vị trí phía trước không bị lệch
        {
            var m = matches[i];
            var original = text.Substring(m.Index, m.Length);
            var capitalized = original.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(w => char.IsUpper(w[0]));
            if (!accept(new NameMatch(m.Value, capitalized))) continue;
            sb.Remove(m.Index, m.Length).Insert(m.Index, NameToken);
            count++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Họ Việt phổ biến. Chữ CÓ dấu so sánh đúng dấu ("Làm", "Tổng", "Đồng" không phải họ). Chữ KHÔNG dấu (CV tiếng Anh, OCR mất dấu)
    /// so với dạng bỏ dấu; các họ ngắn dễ trùng từ thường ("Le", "Do", "Ha"...) chỉ tính khi chuỗi có từ 3 chữ.
    /// </summary>
    private static bool IsSurname(string word, int sequenceLength)
    {
        var lower = word.ToLowerInvariant();
        if (Surnames.Contains(lower)) return true;
        var folded = Fold(word);
        if (folded != lower) return false;   // chữ có dấu nhưng không đúng họ nào
        return UnaccentedSurnames.Contains(folded) || (sequenceLength >= 3 && AmbiguousUnaccentedSurnames.Contains(folded));
    }

    /// <summary>Bỏ dấu + viết thường, GIỮ NGUYÊN độ dài chuỗi (đ → d, ặ → a...).</summary>
    private static string Fold(string s)
    {
        var chars = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c < 128) { chars[i] = char.ToLowerInvariant(c); continue; }
            if (c is 'đ' or 'Đ') { chars[i] = 'd'; continue; }
            var d = c.ToString().Normalize(NormalizationForm.FormD);
            chars[i] = char.ToLowerInvariant(d.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark ? d[0] : c);
        }
        return new string(chars);
    }
}
