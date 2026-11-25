using System.Globalization;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>Server-side Blazor circuit JWT only; no bearer data is ever passed to JS.</summary>
public sealed class AdminOperationsReportV2ApiClient(HttpClient http, ILogger<AdminOperationsReportV2ApiClient> logger)
    : ApiClientBase(http, logger)
{
    public static string Query(DateOnly from, DateOnly to, string report, string format)
    {
        if (!AdminReportV2Kinds.Allowed.Contains(report, StringComparer.Ordinal) || format is not ("xlsx" or "csv"))
            throw new ArgumentException("Loại hoặc định dạng báo cáo không hợp lệ.");
        return $"?from={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
            $"&to={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
            $"&report={Uri.EscapeDataString(report)}&format={format}";
    }

    public Task<ApiResult<AdminReportV2PreviewDto>> PreviewAsync(DateOnly from, DateOnly to,
        string report, string format, CancellationToken ct = default) =>
        GetPrivateAsync<AdminReportV2PreviewDto>(
            "api/admin/reports/v2/preview" + Query(from, to, report, format),
            "Admin operations report v2 preview", ct);

    public Task<ApiResult<DownloadedFile>> DownloadAsync(DateOnly from, DateOnly to,
        string report, string format, CancellationToken ct = default) =>
        GetFileAsync("api/admin/reports/v2/download" + Query(from, to, report, format), ct);
}
