using System.Text;

namespace SmartCv.Core.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int MinKeyLength = 32;
    public string? Key { get; set; }
    public string Issuer { get; set; } = "SmartCv.Api";
    public string Audience { get; set; } = "SmartCv.Web";
    public int ExpiresHours { get; set; } = 8;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Key)) return "Thiếu Jwt:Key. Cấu hình khóa ngẫu nhiên bằng User Secrets hoặc kho bí mật của môi trường triển khai.";
        if (Key.Length < MinKeyLength || Encoding.UTF8.GetByteCount(Key) < 32) return "Jwt:Key quá ngắn, cần ít nhất 32 ký tự và 32 byte UTF-8.";
        if (string.IsNullOrWhiteSpace(Issuer)) return "Jwt:Issuer là bắt buộc.";
        if (string.IsNullOrWhiteSpace(Audience)) return "Jwt:Audience là bắt buộc.";
        if (ExpiresHours is < 1 or > 24) return "Jwt:ExpiresHours phải từ 1 đến 24.";
        return null;
    }
}
