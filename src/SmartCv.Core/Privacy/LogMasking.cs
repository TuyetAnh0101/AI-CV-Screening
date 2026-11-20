using System.Text.RegularExpressions;

namespace SmartCv.Core.Privacy;

/// <summary>Operational logging only. Reuses the CV privacy rules; never changes persisted CV or audit data.</summary>
public static partial class LogMasking
{
    public const string Hidden = "[đã ẩn]";
    private static readonly string[] PrivateNames = ["password", "secret", "token", "apikey", "connectionstring",
        "email", "phone", "name", "text", "body", "payload", "prompt", "reason", "evidence", "note", "search",
        "url", "uri", "path", "authorization", "cookie", "credential", "address", "birth", "dob", "gender"];
    public static bool IsSensitiveName(string name)
    {
        var normalized = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return PrivateNames.Any(normalized.Contains);
    }
    public static string MaskText(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            var limited = value.Length <= 4096 ? value : value[..4096];
            var safe = SecretRegex().Replace(limited, "$1=" + Hidden);
            safe = BearerRegex().Replace(safe, "Bearer " + Hidden);
            safe = UrlRegex().Replace(safe, "[địa chỉ đã ẩn]");
            safe = FileRegex().Replace(safe, "[file đã ẩn]");
            safe = PersonalDataMasker.Mask(safe).Text;
            return string.Concat(safe.Select(c => char.IsControl(c) ? ' ' : c));
        }
        catch (RegexMatchTimeoutException) { return Hidden; }
    }
    [GeneratedRegex(@"\b(Server|Data Source|Initial Catalog|Database|User Id|uid|Password|pwd|ApiKey|api_key|Jwt:Key|Secret|Token)\s*[:=]\s*[^;\r\n]+", RegexOptions.IgnoreCase, 100)]
    private static partial Regex SecretRegex();
    [GeneratedRegex(@"\bBearer\s+[^\s,;]+", RegexOptions.IgnoreCase, 100)]
    private static partial Regex BearerRegex();
    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase, 100)]
    private static partial Regex UrlRegex();
    [GeneratedRegex(@"[^\s<>""']+\.(pdf|png|jpe?g|docx?|webp)\b", RegexOptions.IgnoreCase, 100)]
    private static partial Regex FileRegex();
}
