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
