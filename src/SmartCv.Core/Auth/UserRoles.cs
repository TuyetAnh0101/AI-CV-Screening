namespace SmartCv.Core.Auth;

public static class UserRoles
{
    public const string Admin = "Admin";
    public const string Hr = "HR";

    public static bool IsValid(string? role) => role is Admin or Hr;
}
