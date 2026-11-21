namespace SmartCv.Core.Entities;

/// <summary>Liên kết tài khoản với nhà cung cấp đăng nhập ngoài (google | linkedin). Dùng từ A58.</summary>
public class UserExternalLogin
{
    public int UserExternalLoginId { get; set; }
    public int UserId { get; set; }
    public string Provider { get; set; } = string.Empty;

    /// <summary>Định danh bất biến phía nhà cung cấp (claim sub).</summary>
    public string ProviderKey { get; set; } = string.Empty;

    public string? EmailAtLink { get; set; }
    public DateTime LinkedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    public User User { get; set; } = null!;
}
