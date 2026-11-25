using System.Globalization;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>
/// Admin-only report endpoints. ApiClientBase attaches the current Blazor circuit JWT.
/// JavaScript never receives the token or makes authenticated API calls.
/// </summary>
public sealed class AdminOperationsReportApiClient(HttpClient http, ILogger<AdminOperationsReportApiClient> logger)
    : ApiClientBase(http, logger)
{
    public static string Period(DateOnly from, DateOnly to) =>
        $"?from={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&to={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    public Task<ApiResult<AdminReportSummaryDto>> SummaryAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        GetPrivateAsync<AdminReportSummaryDto>("api/admin/reports/summary" + Period(from, to), "Admin operations report summary", ct);

    public Task<ApiResult<AdminReportTrendDto>> TrendAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        GetPrivateAsync<AdminReportTrendDto>("api/admin/reports/trend" + Period(from, to), "Admin operations report trend", ct);

    public Task<ApiResult<AdminReportExportPageDto>> ExportsAsync(DateOnly from, DateOnly to,
        int page, int pageSize, CancellationToken ct = default) =>
        GetPrivateAsync<AdminReportExportPageDto>("api/admin/reports/exports" + Period(from, to) +
            $"&page={page.ToString(CultureInfo.InvariantCulture)}&pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}",
            "Admin report export activity", ct);

    public Task<ApiResult<AdminReportExportPreviewDto>> PreviewAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        GetPrivateAsync<AdminReportExportPreviewDto>("api/admin/reports/preview" + Period(from, to), "Admin report preview", ct);

    public Task<ApiResult<AdminReportHealthDto>> HealthAsync(CancellationToken ct = default) =>
        GetPrivateAsync<AdminReportHealthDto>("api/admin/reports/health", "Admin system readiness", ct);

    public Task<ApiResult<DownloadedFile>> DownloadCsvAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        GetFileAsync("api/admin/reports/download" + Period(from, to) + "&format=csv", ct);
}
