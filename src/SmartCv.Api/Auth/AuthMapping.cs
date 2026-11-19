using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;

namespace SmartCv.Api.Auth;

public static class AuthMapping
{
    public static MeDto ToMe(User user) => new()
    {
        UserId = user.UserId, Email = user.Email, FullName = user.FullName, Role = user.Role,
        MustChangePassword = user.MustChangePassword,
        LastLoginAt = user.LastLoginAt is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null
    };
}
