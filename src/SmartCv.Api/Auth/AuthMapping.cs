using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;

namespace SmartCv.Api.Auth;

public static class AuthMapping
{
    public static MeDto ToMe(User user, SmartCv.Core.Workspaces.WorkspaceSessionInfo? workspace = null) => new()
    {
        UserId = user.UserId, Email = user.Email, FullName = user.FullName, Role = user.Role,
        MustChangePassword = user.MustChangePassword,
        LastLoginAt = user.LastLoginAt is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null,
        Workspace = workspace is null ? null : new MeWorkspaceDto { Id = workspace.Id, Name = workspace.Name, Kind = workspace.Kind, Role = workspace.Role }
    };
    public static UserDto ToDto(User user) => ToDto(user, null);
    public static UserDto ToDto(User user, string? workspaceName) => new()
    {
        WorkspaceName = workspaceName, EmailVerified = user.EmailVerifiedAt is not null, SignupSource = user.SignupSource,
        UserId = user.UserId, Email = user.Email, FullName = user.FullName, Role = user.Role,
        RoleName = SmartCv.Core.Auth.UserRoles.DisplayName(user.Role), IsActive = user.IsActive,
        MustChangePassword = user.MustChangePassword, Version = Version(user),
        CreatedAt = DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc),
        LastLoginAt = user.LastLoginAt is { } last ? DateTime.SpecifyKind(last, DateTimeKind.Utc) : null
    };

    // A digest of the revision state is safe to expose as an opaque ETag. The stamp itself is never returned in UserDto.
    public static string Version(User user) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        { user.UserId, user.Email, user.FullName, user.Role, user.IsActive, user.MustChangePassword, user.SecurityStamp }))));
}
