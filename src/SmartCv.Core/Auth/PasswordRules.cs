namespace SmartCv.Core.Auth;

/// <summary>Quy tắc A27: 8–128 ký tự, có chữ và số, không trùng email.</summary>
public static class PasswordRules
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    public static string? Validate(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password)) return "Mật khẩu là bắt buộc.";
        if (password.Length < MinLength) return $"Mật khẩu cần ít nhất {MinLength} ký tự.";
        if (password.Length > MaxLength) return $"Mật khẩu tối đa {MaxLength} ký tự.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) return "Mật khẩu cần có cả chữ và số.";
        if (password.Trim() != password) return "Mật khẩu không được bắt đầu hoặc kết thúc bằng khoảng trắng.";
        if (!string.IsNullOrWhiteSpace(email) && string.Equals(password, email.Trim(), StringComparison.OrdinalIgnoreCase))
            return "Mật khẩu không được trùng email.";
        return null;
    }
}
