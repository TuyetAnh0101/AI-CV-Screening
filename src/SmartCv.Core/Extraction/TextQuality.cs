using System.Text;
using SmartCv.Core.Constants;

namespace SmartCv.Core.Extraction;

public static class TextQualityLevels
{
    public const string Good = "Good";
    public const string Low = "Low";       // có thể đọc thiếu / đọc sai: giao diện cảnh báo vàng
    public const string Empty = "Empty";   // không có chữ
}

/// <summary>
/// Chỉ số chất lượng chữ đọc được. LetterRatio thang 0–1, cắt (không làm tròn lên) còn 2 chữ số
/// để con số hiển thị luôn khớp với cảnh báo (0,6499 hiển thị 0,64, không phải 0,65).
/// </summary>
public sealed record TextQualityResult(int CharCount, int WordCount, decimal LetterRatio, string Level, IReadOnlyList<string> Warnings)
{
    public bool MaybeMisread => Level != TextQualityLevels.Good;
}

/// <summary>
/// A42: đánh giá nhanh chữ đọc được từ CV có đáng tin không, để HR thấy CV ảnh mờ TRƯỚC khi chấm.
///   - Không có chữ (null / chỉ khoảng trắng) → Empty.
///   - Dưới 500 ký tự: CV thật thường 1.500–4.000 ký tự → có thể đọc thiếu.
///   - Tỷ lệ chữ cái dưới 0,65 (char.IsLetter / ký tự không phải khoảng trắng): OCR ảnh mờ ra nhiều ký hiệu lạ.
///   - Có từ 20 từ trở lên và trên 30% là "từ" một chữ cái: chữ bị vỡ rời rạc.
///   - Có cảnh báo và đọc bằng OCR (PdfOcr, ImageOcr): thêm lời khuyên mở file gốc.
/// Đây là heuristic để CẢNH BÁO, không kết luận CV sai; kết quả OCR cùng một ảnh có thể khác nhau giữa các máy.
/// Hàm thuần, không ghi log, cảnh báo chỉ chứa con số (không có nội dung CV).
/// </summary>
public static class TextQuality
{
    public const int MinChars = 500;
    public const decimal MinLetterRatio = 0.65m;
    public const decimal MaxSingleLetterWordRatio = 0.30m;
    public const int MinWordsForSingleLetterCheck = 20;

    public const string EmptyWarning = "Không đọc được chữ nào từ CV.";
    public const string OcrAdvice = "CV được đọc bằng OCR từ ảnh: nên mở file gốc để kiểm tra trước khi chấm.";

    public static TextQualityResult Assess(string? text, string? extractionMethod = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TextQualityResult(0, 0, 0m, TextQualityLevels.Empty, [EmptyWarning]);

        // Chữ tiếng Việt dạng tổ hợp (NFD) có dấu là ký tự riêng không phải chữ cái: chuẩn hóa để không bị đếm sai
        var s = text.Normalize(NormalizationForm.FormC);
        int nonSpace = 0, letters = 0;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            nonSpace++;
            if (char.IsLetter(c)) letters++;
        }

        var words = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var singleLetterWords = words.Count(w => w.Length == 1 && char.IsLetter(w[0]));
        var ratio = (decimal)letters / nonSpace;   // nonSpace > 0 vì text có ký tự không phải khoảng trắng

        var warnings = new List<string>();
        if (s.Length < MinChars)
            warnings.Add($"Chỉ đọc được {s.Length} ký tự (CV thường trên {MinChars}): có thể đọc thiếu.");
        if (ratio < MinLetterRatio)
            warnings.Add($"Chỉ {Math.Floor(ratio * 100)}% là chữ cái: có nhiều ký hiệu lạ, có thể ảnh mờ hoặc đọc sai.");
        if (words.Length >= MinWordsForSingleLetterCheck && (decimal)singleLetterWords / words.Length > MaxSingleLetterWordRatio)
            warnings.Add("Nhiều chữ cái đứng rời rạc: chữ có thể bị vỡ khi đọc ảnh.");
        if (warnings.Count > 0 && extractionMethod is ExtractionMethods.PdfOcr or ExtractionMethods.ImageOcr)
            warnings.Add(OcrAdvice);

        return new TextQualityResult(s.Length, words.Length, Math.Round(ratio, 2, MidpointRounding.ToZero),
            warnings.Count == 0 ? TextQualityLevels.Good : TextQualityLevels.Low, warnings);
    }
}
