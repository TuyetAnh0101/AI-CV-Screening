using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SmartCv.Core.Options;

namespace SmartCv.Api.Auth;

public sealed class LoginRateLimitPolicy(IOptions<AuthOptions> options) : IRateLimiterPolicy<string>
{
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => static async (context, _) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await AuthProblems.Problem(429, "login_rate_limited", "Thử quá nhiều lần", "Bạn đã thử đăng nhập quá nhiều lần. Đợi 1 phút rồi thử lại.").ExecuteAsync(context.HttpContext);
    };

    public RateLimitPartition<string> GetPartition(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        var key = address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4().ToString() : address?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.Value.LoginAttemptsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        });
    }
}
