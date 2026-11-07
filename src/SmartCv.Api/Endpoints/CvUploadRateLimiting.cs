using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// Giới hạn tần suất cho API tải CV, dùng Rate Limiting tích hợp của ASP.NET Core.
/// Chỉ áp dụng cho endpoint gắn policy <see cref="PolicyName"/>; health check và các API khác không bị giới hạn.
///   1. Theo địa chỉ IP: tối đa PermitLimit request / WindowSeconds giây (fixed window).
///   2. Toàn API: tối đa ConcurrentRequests request tải CV chạy cùng lúc, QueueLimit request chờ.
/// Vượt giới hạn: 429 Too Many Requests (ProblemDetails, kèm Retry-After khi biết thời gian chờ).
/// </summary>
public static class CvUploadRateLimiting
{
    public const string PolicyName = "cv-upload";

    public static IServiceCollection AddCvUploadRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CvUploadEndpointOptions>()
            .Bind(configuration.GetSection(CvUploadEndpointOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<CvUploadEndpointOptions>>((limiter, endpointOptions) =>
            {
                var opt = endpointOptions.Value;
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                // (1) Theo IP. Khi chạy sau proxy/load balancer cần cấu hình Forwarded Headers,
                // nếu không mọi người dùng sẽ chung một IP của proxy.
                limiter.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = opt.PermitLimit,
                        Window = TimeSpan.FromSeconds(opt.WindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

                // (2) Đồng thời trên toàn API, chỉ cho endpoint tải CV; endpoint khác không bị giới hạn.
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    IsUploadEndpoint(context)
                        ? RateLimitPartition.GetConcurrencyLimiter("cv-upload-concurrency", _ => new ConcurrencyLimiterOptions
                        {
                            PermitLimit = opt.ConcurrentRequests,
                            QueueLimit = opt.QueueLimit,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                        })
                        : RateLimitPartition.GetNoLimiter("not-limited"));

                limiter.OnRejected = async (context, cancellationToken) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    }

                    await TypedResults.Problem(
                            title: "Quá nhiều yêu cầu tải CV",
                            detail: "Hệ thống đang nhận quá nhiều yêu cầu tải CV. Vui lòng thử lại sau ít phút.",
                            statusCode: StatusCodes.Status429TooManyRequests)
                        .ExecuteAsync(context.HttpContext);
                };
            });

        return services;
    }

    private static bool IsUploadEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == PolicyName;
}
