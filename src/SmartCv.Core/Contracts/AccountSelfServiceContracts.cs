namespace SmartCv.Core.Contracts;

/// <summary>Public A57: chỉ email; không trả về việc tài khoản có tồn tại.</summary>
public sealed class ForgotPasswordRequest
{
    public string? Email { get; set; }
}

public sealed class ResetPasswordRequest
{
    public string? Token { get; set; }
    public string? NewPassword { get; set; }
}

public sealed class UpdateMyProfileRequest
{
    public string? FullName { get; set; }
}

public sealed record AccountNotice(string Message);

public sealed record LinkedLoginDto(string Provider, string? EmailAtLink, DateTime LinkedAt, DateTime? LastUsedAt);

public sealed record ProfileDto(
    int UserId,
    string FullName,
    string Email,
    bool EmailVerified,
    bool HasPassword,
    string Role,
    string SignupSource,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    MeWorkspaceDto? Workspace,
    IReadOnlyList<LinkedLoginDto> Logins);
