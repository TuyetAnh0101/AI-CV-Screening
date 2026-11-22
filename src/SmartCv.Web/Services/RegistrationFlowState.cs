using SmartCv.Core.Auth;

namespace SmartCv.Web.Services;

/// <summary>
/// Email đăng ký chỉ tồn tại trong scope của tab/circuit Blazor.
/// Không lưu vào URL, localStorage hay singleton. Sau F5 người dùng phải nhập lại email nếu cần gửi lại.
/// </summary>
public sealed class RegistrationFlowState(TimeProvider clock)
{
    public string? Email { get; private set; }
    public DateTimeOffset? NextResendAt { get; private set; }

    public string MaskedEmail => Mask(Email);
    public int CooldownSeconds => NextResendAt is { } at && at > clock.GetUtcNow()
        ? (int)Math.Ceiling((at - clock.GetUtcNow()).TotalSeconds) : 0;

    public void Remember(string? rawEmail)
    {
        var normalized = RegistrationRules.NormalizeResendEmail(rawEmail);
        if (normalized is null) return;
        Email = normalized;
    }

    public void StartCooldown(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        var until = clock.GetUtcNow().Add(duration);
        if (NextResendAt is not { } current || current < until) NextResendAt = until;
    }

    public static string Mask(string? email)
    {
        var normalized = RegistrationRules.NormalizeResendEmail(email);
        if (normalized is null) return "";
        var at = normalized.IndexOf('@');
        var local = normalized[..at];
        return local[..1] + "***@" + normalized[(at + 1)..];
    }

    /// <summary>Chỉ dùng để HIỂN THỊ, không dùng giá trị URL làm địa chỉ gửi email.</summary>
    public static string SafeMaskedDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 ||
            value.Any(char.IsControl) || value.Contains('<') || value.Contains('>')) return "";
        var parts = value.Split('@');
        if (parts.Length != 2 || parts[0].Length is < 2 or > 40 ||
            parts[1].Length is < 3 or > 200 || !parts[0].Contains('*') ||
            parts[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-')) return "";
        return value;
    }
}
