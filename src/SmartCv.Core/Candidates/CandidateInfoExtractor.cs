using System.Text.RegularExpressions;
using SmartCv.Core.Privacy;

namespace SmartCv.Core.Candidates;

/// <summary>Gợi ý thông tin ứng viên đọc từ chữ trong CV. Trường nào không tìm được → null.</summary>
public sealed record CandidateSuggestion(string? FullName, string? Email, string? PhoneNumber);

/// <summary>
/// A24 (theo C7): HR nhập thông tin ứng viên, hệ thống ĐIỀN SẴN gợi ý đọc từ CV để HR sửa rồi lưu.
/// Không tự lưu: chữ OCR có thể đọc sai, HR phải xem lại.
///   - Họ tên: phần đầu CV trông giống tên (dùng lại PersonalDataMasker.GuessCandidateName).
///   - Email: email đầu tiên trong CV.
///   - Điện thoại: số di động Việt Nam đầu tiên, đã chuẩn hóa về 10 số.
/// </summary>
public static partial class CandidateInfoExtractor
{
    [GeneratedRegex(@"(?<![\w.+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}(?![\w])")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<![\d+])(?:\(?\+?84\)?[\s.\-]?|0)[35789](?:[\s.\-]?\d){8}(?!\d)")]
    private static partial Regex VnMobileRegex();

    public static CandidateSuggestion Suggest(string? cvText)
    {
        if (string.IsNullOrWhiteSpace(cvText)) return new(null, null, null);

        var name = PersonalDataMasker.GuessCandidateName(cvText);
        if (name is not null) name = ToTitleCase(name);

        var email = EmailRegex().Match(cvText) is { Success: true } em ? CandidateRules.NormalizeEmail(em.Value) : null;
        var phone = VnMobileRegex().Match(cvText) is { Success: true } pm ? CandidateRules.NormalizePhone(pm.Value) : null;

        return new(name, email, phone);
    }

    /// <summary>"NGUYỄN VĂN AN" → "Nguyễn Văn An"; tên đã viết hoa chữ đầu giữ nguyên.</summary>
    public static string ToTitleCase(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}
