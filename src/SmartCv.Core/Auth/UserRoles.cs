namespace SmartCv.Core.Auth;

public static class UserRoles
{
    public const string Admin = "Admin";
    public const string Hr = "HR";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Admin, Hr });
    public static string DisplayName(string? role) => role switch { Admin => "Quản trị viên", Hr => "Nhân sự (HR)", _ => "Không xác định" };

    public static bool IsValid(string? role) => role is Admin or Hr;
}
