using System.Globalization;
using System.Security.Claims;
using SmartCv.Core.Auth;

namespace SmartCv.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public const int MaxActorLength = 100;
    public static int? GetUserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(SmartCvClaims.UserId), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    public static string? GetEmail(this ClaimsPrincipal user) => user.FindFirstValue(SmartCvClaims.Email);
    public static string? GetRole(this ClaimsPrincipal user) => user.FindFirstValue(SmartCvClaims.Role);
    public static bool IsAdmin(this ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && user.GetRole() == UserRoles.Admin;

    // Helper của hợp đồng A28; A29 mới dùng để thay Actor gõ tay trong các endpoint nghiệp vụ.
    public static string ActorName(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(SmartCvClaims.Name);
        if (string.IsNullOrWhiteSpace(value)) value = user.GetEmail();
        if (string.IsNullOrWhiteSpace(value)) return "Không rõ";
        value = value.Trim();
        if (value.Length <= MaxActorLength) return value;
        var length = char.IsHighSurrogate(value[MaxActorLength - 1]) ? MaxActorLength - 1 : MaxActorLength;
        return value[..length];
    }
}
