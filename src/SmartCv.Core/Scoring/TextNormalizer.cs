using System.Text;

namespace SmartCv.Core.Scoring;

/// <summary>
/// Đưa chữ về dạng chuẩn để so khớp: viết thường, BỎ DẤU tiếng Việt, đ → d.
/// Lý do: OCR hay làm mất dấu chữ in hoa ("KIỂM THỬ" thành "KIEM THU"), người viết CV cũng lúc có dấu lúc không.
/// Mỗi ký tự gốc đổi thành ĐÚNG MỘT ký tự, nên vị trí trong chuỗi đã chuẩn hóa trùng vị trí trong chuỗi gốc.
/// Nhờ vậy cắt được đoạn trích dẫn (Evidence) từ chữ gốc còn dấu.
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Đưa về Unicode NFC ("e" + dấu ghép thành "ế"). Phải gọi TRƯỚC khi lấy vị trí, để một chữ là một ký tự.
    /// A7 đã làm việc này khi lưu; làm lại ở đây để hàm chấm đúng với mọi nguồn chữ.
    /// </summary>
    public static string Compose(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try { return text.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return text; }   // chuỗi có surrogate lẻ: giữ nguyên, Fold vẫn xử lý được
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(Fold(ch));
        return sb.ToString();
    }

    public static char Fold(char c)
    {
        if (c < 128) return char.ToLowerInvariant(c);

        switch (c)
        {
            case 'đ' or 'Đ' or 'Ð': return 'd';
            case '♯' or '＃': return '#';                               // C♯, C＃ (font / OCR)
            case >= '‐' and <= '―' or '−': return '-';   // các loại gạch ngang
        }

        if (char.IsSurrogate(c)) return ' ';   // emoji, ký tự đặc biệt: không cần so khớp

        try
        {
            // "ế" tách thành "e" + dấu mũ + dấu sắc → lấy "e"
            return char.ToLowerInvariant(c.ToString().Normalize(NormalizationForm.FormD)[0]);
        }
        catch (ArgumentException)
        {
            return ' ';   // ký tự Unicode không hợp lệ
        }
    }
}
