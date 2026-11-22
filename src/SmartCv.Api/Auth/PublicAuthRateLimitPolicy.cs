using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SmartCv.Api.Auth;

/// <summary>Giới hạn theo IP cho một API instance. Khi scale nhiều node phải thêm edge/distributed limiter.</summary>
public abstract class PublicAuthRateLimitPolicy(int permitLimit, TimeSpan window, string bucket)
    : IRateLimiterPolicy<string>
{
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => static async (context, _) =>
    {
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
            ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
        await AuthProblems.Problem(429, "public_auth_rate_limited", "Quá nhiều yêu cầu",
            "Vui lòng thử lại sau.").ExecuteAsync(context.HttpContext);
    };

    public RateLimitPartition<string> GetPartition(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        var addr = ip?.IsIPv4MappedToIPv6 == true ? ip.MapToIPv4().ToString() : ip?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(bucket + ":" + addr, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit, Window = window, QueueLimit = 0, AutoReplenishment = true
        });
    }
}

public sealed class RegisterRateLimitPolicy() : PublicAuthRateLimitPolicy(5, TimeSpan.FromMinutes(10), "register");
public sealed class VerifyEmailRateLimitPolicy() : PublicAuthRateLimitPolicy(10, TimeSpan.FromMinutes(10), "verify-email");
public sealed class ResendVerificationRateLimitPolicy() : PublicAuthRateLimitPolicy(10, TimeSpan.FromHours(1), "resend-verification");
