using SmartCv.Core.Ops;
using SmartCv.Infrastructure.Ops;

namespace SmartCv.Api.Ops;

public static class HealthEndpoints
{
    public static void MapReadinessEndpoint(this IEndpointRouteBuilder app) => app.MapGet("/api/health",
        async (HttpContext http, ApplicationReadiness readiness, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var report = await readiness.CheckAsync(ct);
            return Results.Json(report, statusCode: report.Status == "ok" ? 200 : 503);
        }).AllowAnonymous().WithTags("Health").WithName("Health")
        .Produces<HealthReportDto>(200).Produces<HealthReportDto>(503);
}
