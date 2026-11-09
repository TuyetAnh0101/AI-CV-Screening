using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SmartCv.Infrastructure.Gemini;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// A10: giới hạn tần suất POST ai-preview theo IP, vì mỗi lần gọi tốn quota Gemini.
/// Tách khỏi policy tải CV để một lần chấm thử kéo dài (chờ thử lại) không chiếm lượt của API tải CV.
/// </summary>
public sealed class AiPreviewRateLimitPolicy(IOptions<GeminiOptions> options) : IRateLimiterPolicy<string>
{
    public const string PolicyName = "ai-preview";

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => static async (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        await TypedResults.Problem(title: "Quá nhiều yêu cầu chấm thử",
                detail: "Mỗi lần chấm thử tốn hạn mức Gemini. Vui lòng thử lại sau ít phút.", statusCode: StatusCodes.Status429TooManyRequests)
            .ExecuteAsync(context.HttpContext);
    };

    public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.Value.PreviewPermitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
}
