using System.Security.Cryptography;

namespace SmartCv.Core.Entities;

/// <summary>Tài khoản SCApp. Khóa bằng IsActive để giữ lịch sử người thực hiện.</summary>
public class User
{
    private string email = string.Empty;

    public int UserId { get; set; }
    public string Email
    {
        get => email;
        set => email = value.Trim().ToLowerInvariant();
    }
    public string FullName { get; set; } = string.Empty;
    /// <summary>Hash có salt do PasswordHasher&lt;User&gt; tạo, không lưu mật khẩu gốc.</summary>
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } // UTC
    public DateTime? LastLoginAt { get; set; } // UTC
    public bool MustChangePassword { get; set; }
    public string SecurityStamp { get; set; } = NewStamp();

    public static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
