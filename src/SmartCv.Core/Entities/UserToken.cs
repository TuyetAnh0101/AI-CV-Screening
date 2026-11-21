namespace SmartCv.Core.Entities;

/// <summary>Token dùng một lần (xác minh email, đặt lại mật khẩu...). Chỉ lưu SHA-256 của token. Dùng từ A56.</summary>
public class UserToken
{
    public long UserTokenId { get; set; }
    public int UserId { get; set; }

    /// <summary>EmailVerification | PasswordReset | ExternalLoginCode | ExternalLinkTicket (định nghĩa ở A56/A58).</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>SHA-256 hex, 64 ký tự.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Chỉ cho ExternalLoginCode: returnUrl đã kiểm tra.</summary>
    public string? Payload { get; set; }

    public User User { get; set; } = null!;
}
