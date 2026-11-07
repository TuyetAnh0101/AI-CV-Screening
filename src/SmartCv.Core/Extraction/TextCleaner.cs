using System.Text;
using System.Text.RegularExpressions;

namespace SmartCv.Core.Extraction;

/// <summary>
/// Làm sạch chữ đọc được từ CV trước khi lưu, để bước chấm điểm (A9, A10) xử lý ổn định.
/// </summary>
public static partial class TextCleaner
{
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // 1. Chuẩn Unicode NFC. Tiếng Việt có 2 cách mã hóa dấu: "ế" là 1 ký tự,
        //    hoặc "e" + dấu mũ + dấu sắc là 3 ký tự. Nhìn giống nhau nhưng so chuỗi sẽ khác.
        //    PDF hay dùng cách 2, nên phải đưa về cách 1 thì tìm từ khóa mới đúng.
        var text = raw.Normalize(NormalizationForm.FormC);

        // 2. Bỏ ký tự điều khiển (trừ xuống dòng, tab) và ký tự lạ PDF hay chèn
        text = ControlChars().Replace(text, string.Empty);
        text = text.Replace(' ', ' ')    // khoảng trắng không ngắt dòng
                   .Replace("­", "")     // gạch nối mềm
                   .Replace('–', '-')    // gạch ngang –
                   .Replace('—', '-');   // gạch ngang dài —

        // 3. Thống nhất xuống dòng, gom khoảng trắng thừa, tối đa 1 dòng trống liên tiếp
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = SpacesAndTabs().Replace(text, " ");
        text = SpaceAroundNewline().Replace(text, "\n");
        text = ManyNewlines().Replace(text, "\n\n");

        return text.Trim();
    }

    /// <summary>Đếm ký tự có nghĩa (bỏ khoảng trắng). Dùng để đoán PDF có phải bản scan không.</summary>
    public static int CountMeaningfulChars(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Count(c => !char.IsWhiteSpace(c));

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F�]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex SpacesAndTabs();

    [GeneratedRegex(@" ?\n ?")]
    private static partial Regex SpaceAroundNewline();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();
}
