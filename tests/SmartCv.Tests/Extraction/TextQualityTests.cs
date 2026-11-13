using System.Text;
using SmartCv.Core.Constants;
using SmartCv.Core.Extraction;

namespace SmartCv.Tests;

/// <summary>
/// A42: chỉ số chất lượng chữ đọc được. Chữ tổng hợp (không phải CV thật): CV rõ, CV ảnh mờ giả lập, CV rỗng,
/// và các ranh giới 500 ký tự, tỷ lệ chữ cái 0,65, từ một chữ cái 30% (khi có ít nhất 20 từ).
/// </summary>
public class TextQualityTests
{
    // CV đọc tốt: ~1.000 ký tự tiếng Việt có dấu, cấu trúc như ảnh rõ (kinh nghiệm, kỹ năng, học vấn, dự án)
    private static readonly string ClearCv = string.Join("\n", Enumerable.Repeat(
        "Kinh nghiệm: lập trình viên C# và ASP.NET Core tại công ty phần mềm, xây dựng API bán hàng với SQL Server.", 10));

    // Giả lập OCR ảnh mờ: ký hiệu lạ, chữ cái đứng rời rạc, ngắn
    private static readonly string BlurryOcr = string.Join(" ", Enumerable.Repeat("K1nh ngh!3m: 2O23 | C# , A S P . N E T ~~ %% ## l a p tr1nh", 4));

    /// <summary>Đúng <paramref name="length"/> ký tự, toàn chữ cái thành từ 5 chữ (không cảnh báo nào khác ngoài độ dài).</summary>
    private static string Letters(int length)
    {
        var sb = new StringBuilder(length);
        while (sb.Length < length) sb.Append(sb.Length % 6 == 5 ? ' ' : 'a');
        if (sb[^1] == ' ') sb[^1] = 'a';
        return sb.ToString(0, length);
    }

    /// <summary>1.000 ký tự không phải khoảng trắng, trong đó đúng <paramref name="letters"/> chữ cái (còn lại là chữ số), nhóm 10 ký tự.</summary>
    private static string WithLetterCount(int letters)
    {
        var chars = Enumerable.Range(0, 1000).Select(i => i < letters ? 'a' : '7').ToArray();
        return string.Join(" ", chars.Chunk(10).Select(c => new string(c)));
    }

    [Fact]
    public void Clear_cv_text_is_good_without_warnings()
    {
        var q = TextQuality.Assess(ClearCv, ExtractionMethods.PdfText);

        Assert.Equal(TextQualityLevels.Good, q.Level);
        Assert.False(q.MaybeMisread);
        Assert.Empty(q.Warnings);
        Assert.Equal(ClearCv.Length, q.CharCount);
        Assert.True(q.WordCount > 100);
        Assert.InRange(q.LetterRatio, 0.65m, 1m);
    }

    [Fact]
    public void Blurry_image_ocr_is_low_with_all_warnings_and_ocr_advice()
    {
        var q = TextQuality.Assess(BlurryOcr, ExtractionMethods.ImageOcr);

        Assert.Equal(TextQualityLevels.Low, q.Level);
        Assert.True(q.MaybeMisread);
        Assert.Contains(q.Warnings, w => w.Contains("có thể đọc thiếu"));
        Assert.Contains(q.Warnings, w => w.Contains("ký hiệu lạ"));
        Assert.Contains(q.Warnings, w => w.Contains("rời rạc"));
        Assert.Equal(TextQuality.OcrAdvice, q.Warnings[^1]);
        Assert.True(q.LetterRatio < 0.65m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void No_text_is_empty(string? text)
    {
        var q = TextQuality.Assess(text, ExtractionMethods.ImageOcr);

        Assert.Equal(TextQualityLevels.Empty, q.Level);
        Assert.True(q.MaybeMisread);
        Assert.Equal((0, 0, 0m), (q.CharCount, q.WordCount, q.LetterRatio));
        Assert.Equal([TextQuality.EmptyWarning], q.Warnings);
    }

    [Theory]
    [InlineData(499, TextQualityLevels.Low)]
    [InlineData(500, TextQualityLevels.Good)]
    public void Five_hundred_characters_is_the_boundary(int length, string level)
    {
        var q = TextQuality.Assess(Letters(length), ExtractionMethods.PdfText);

        Assert.Equal(length, q.CharCount);
        Assert.Equal(level, q.Level);
        if (level == TextQualityLevels.Low) Assert.Equal("Chỉ đọc được 499 ký tự (CV thường trên 500): có thể đọc thiếu.", Assert.Single(q.Warnings));
    }

    [Theory]
    [InlineData(649, TextQualityLevels.Low, "0.64")]    // 0,649 < 0,65: cảnh báo, và hiển thị 0,64 chứ không làm tròn lên 0,65
    [InlineData(650, TextQualityLevels.Good, "0.65")]
    public void Letter_ratio_065_is_the_boundary_and_never_rounds_up(int letters, string level, string shown)
    {
        var q = TextQuality.Assess(WithLetterCount(letters), ExtractionMethods.PdfText);

        Assert.Equal(level, q.Level);
        Assert.Equal(decimal.Parse(shown, System.Globalization.CultureInfo.InvariantCulture), q.LetterRatio);
        if (level == TextQualityLevels.Low) Assert.Contains("Chỉ 64% là chữ cái", Assert.Single(q.Warnings));
    }

    [Theory]
    [InlineData(30, 70, TextQualityLevels.Good)]   // đúng 30% → không cảnh báo
    [InlineData(31, 69, TextQualityLevels.Low)]    // trên 30% → chữ rời rạc
    public void Single_letter_words_above_30_percent_are_flagged(int singles, int normal, string level)
    {
        // Đúng 100 từ, toàn chữ cái, trên 500 ký tự: chỉ quy tắc chữ rời rạc có thể bật
        var text = string.Join(" ", Enumerable.Repeat("x", singles).Concat(Enumerable.Repeat("Kinhnghiem", normal)));
        Assert.True(text.Length >= TextQuality.MinChars);

        var q = TextQuality.Assess(text, ExtractionMethods.PdfText);

        Assert.Equal(level, q.Level);
        Assert.Equal(level == TextQualityLevels.Low, q.Warnings.Any(w => w.Contains("rời rạc")));
    }

    [Fact]
    public void Single_letter_rule_needs_at_least_20_words()
    {
        // 19 "từ", toàn chữ một ký tự nhưng các từ khác rất dài: không xét quy tắc chữ rời rạc
        var text = string.Join(" ", Enumerable.Repeat("a", 10)) + " " + string.Join(" ", Enumerable.Repeat(new string('b', 60), 9));

        var q = TextQuality.Assess(text, ExtractionMethods.PdfText);

        Assert.Equal(19, q.WordCount);
        Assert.DoesNotContain(q.Warnings, w => w.Contains("rời rạc"));
    }

    [Fact]
    public void Decomposed_vietnamese_counts_like_composed_text()
    {
        var composed = ClearCv.Normalize(NormalizationForm.FormC);
        var decomposed = ClearCv.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(composed.Length, decomposed.Length);

        Assert.Equal(TextQuality.Assess(composed), TextQuality.Assess(decomposed), new QualityComparer());
    }

    [Theory]
    [InlineData(ExtractionMethods.PdfText, false)]
    [InlineData(ExtractionMethods.PdfOcr, true)]
    [InlineData(ExtractionMethods.ImageOcr, true)]
    [InlineData(null, false)]
    public void Ocr_advice_only_for_ocr_methods_with_warnings(string? method, bool advice)
    {
        var low = TextQuality.Assess("Lập trình viên .NET, 3 năm kinh nghiệm.", method);
        var good = TextQuality.Assess(ClearCv, method);

        Assert.Equal(advice, low.Warnings.Contains(TextQuality.OcrAdvice));
        Assert.DoesNotContain(TextQuality.OcrAdvice, good.Warnings);
    }

    [Fact]
    public void Warnings_contain_numbers_only_never_cv_content()
    {
        const string secret = "nguyen.van.an@example.com 0912345678";
        var q = TextQuality.Assess(secret + " ~~ ## %%", ExtractionMethods.ImageOcr);

        Assert.All(q.Warnings, w => Assert.DoesNotContain("example.com", w));
        Assert.All(q.Warnings, w => Assert.DoesNotContain("0912345678", w));
    }

    private sealed class QualityComparer : IEqualityComparer<TextQualityResult>
    {
        public bool Equals(TextQualityResult? a, TextQualityResult? b) =>
            a is not null && b is not null && a.CharCount == b.CharCount && a.WordCount == b.WordCount
            && a.LetterRatio == b.LetterRatio && a.Level == b.Level && a.Warnings.SequenceEqual(b.Warnings);
        public int GetHashCode(TextQualityResult q) => q.CharCount;
    }
}
