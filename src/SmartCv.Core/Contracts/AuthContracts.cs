namespace SmartCv.Core.Contracts;

public sealed class LoginRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
}

public sealed class LoginResponse
{
    public string Token { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public MeDto User { get; init; } = new();
}

public sealed class MeDto
{
    public int UserId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public bool MustChangePassword { get; init; }
    public DateTime? LastLoginAt { get; init; }
}

public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}

/// <summary>Đổi mật khẩu xong: token mới (token cũ hết hiệu lực vì SecurityStamp đã đổi).</summary>
public sealed class ChangePasswordResponse
{
    public string Token { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public MeDto User { get; init; } = new();
}

public sealed class UserDto
{
    public string Version { get; init; } = string.Empty; // Opaque concurrency token; never the SecurityStamp.
    public int UserId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool MustChangePassword { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
}

/// <summary>POST /api/users. Password để trống → hệ thống tạo mật khẩu tạm (trả về MỘT lần).</summary>
public sealed class CreateUserRequest
{
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public string? Role { get; set; }
    public string? Password { get; set; }
}

/// <summary>PUT /api/users/{id}: đổi tên, vai trò. Email không đổi được (là tên đăng nhập; cần thì tạo tài khoản mới).</summary>
public sealed class UpdateUserRequest
{
    public string? ExpectedVersion { get; set; }
    public string? FullName { get; set; }
    public string? Role { get; set; }
}

/// <summary>Trả về khi tạo tài khoản / đặt lại mật khẩu. TemporaryPassword chỉ có MỘT lần trong response này.</summary>
public sealed class UserCredentialResponse
{
    public UserDto User { get; init; } = new();
    /// <summary>Mật khẩu tạm do hệ thống tạo (null nếu Admin tự nhập). Đăng nhập lần đầu phải đổi.</summary>
    public string? TemporaryPassword { get; init; }
}
