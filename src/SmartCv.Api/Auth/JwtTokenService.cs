using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartCv.Core.Auth;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;

namespace SmartCv.Api.Auth;

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider time)
{
    public (string Token, DateTime ExpiresAt) Create(User user)
    {
        var settings = options.Value;
        if (settings.Validate() is { } error) throw new InvalidOperationException(error);
        if (user.UserId <= 0 || !user.IsActive || !UserRoles.IsValid(user.Role) || string.IsNullOrWhiteSpace(user.SecurityStamp))
            throw new InvalidOperationException("Tài khoản không đủ điều kiện cấp token.");
        var now = DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().ToUnixTimeSeconds()).UtcDateTime;
        var expires = now.AddHours(settings.ExpiresHours);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer, Audience = settings.Audience, IssuedAt = now, NotBefore = now, Expires = expires,
            Subject = new ClaimsIdentity([
                new(SmartCvClaims.UserId, user.UserId.ToString(CultureInfo.InvariantCulture)),
                new(SmartCvClaims.Email, user.Email), new(SmartCvClaims.Name, user.FullName),
                new(SmartCvClaims.Role, user.Role), new(SmartCvClaims.Stamp, user.SecurityStamp),
                new(SmartCvClaims.MustChangePassword, user.MustChangePassword ? "true" : "false")]),
            SigningCredentials = new(SigningKey(settings), SecurityAlgorithms.HmacSha256)
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    public static SymmetricSecurityKey SigningKey(JwtOptions settings) => new(Encoding.UTF8.GetBytes(settings.Key!));
    public static TokenValidationParameters ValidationParameters(JwtOptions settings) => new()
    {
        ValidateIssuer = true, ValidIssuer = settings.Issuer,
        ValidateAudience = true, ValidAudience = settings.Audience, IgnoreTrailingSlashWhenValidatingAudience = false,
        ValidateIssuerSigningKey = true, IssuerSigningKey = SigningKey(settings), RequireSignedTokens = true,
        ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromMinutes(1),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ValidTypes = ["JWT"], NameClaimType = SmartCvClaims.Name, RoleClaimType = SmartCvClaims.Role
    };
}
