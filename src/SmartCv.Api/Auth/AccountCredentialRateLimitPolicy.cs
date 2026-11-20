using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SmartCv.Api.Auth;

/// <summary>Bound password verification and credential issuance per authenticated account; independent of login/upload limits.</summary>
public sealed class AccountCredentialRateLimitPolicy : IRateLimiterPolicy<string>
{
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => static async (context, _) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await AuthProblems.Problem(429, "credential_rate_limited", "Thao tác quá nhiều lần", "Đợi 1 phút trước khi thử lại thao tác mật khẩu.").ExecuteAsync(context.HttpContext);
    };
    public RateLimitPartition<string> GetPartition(HttpContext http) => RateLimitPartition.GetFixedWindowLimiter(
        http.User.GetUserId()?.ToString(CultureInfo.InvariantCulture) ?? "unauthenticated", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
}