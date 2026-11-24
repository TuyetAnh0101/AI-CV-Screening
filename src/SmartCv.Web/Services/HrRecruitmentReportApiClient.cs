using System.Globalization;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>Dedicated aggregate HR report. Does not modify ranking export endpoints.</summary>
public sealed class HrRecruitmentReportApiClient(HttpClient http, ILogger<HrRecruitmentReportApiClient> logger)
    : ApiClientBase(http, logger)
{
    private const string Root = "api/hr/reports";

    public static string Query(int? jobId, int? runId, string? from, string? to,
        string stage, string evaluation, int page, int pageSize, bool anonymous)
    {
        var parts = new List<string>
        {
            "stage=" + Uri.EscapeDataString(stage),
            "evaluation=" + Uri.EscapeDataString(evaluation),
            "page=" + page.ToString(CultureInfo.InvariantCulture),
            "pageSize=" + pageSize.ToString(CultureInfo.InvariantCulture),
            "anonymous=" + (anonymous ? "true" : "false")
        };
        if (jobId.HasValue) parts.Add("jobId=" + jobId.Value.ToString(CultureInfo.InvariantCulture));
        if (runId.HasValue) parts.Add("runId=" + runId.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(from)) parts.Add("from=" + Uri.EscapeDataString(from));
        if (!string.IsNullOrWhiteSpace(to)) parts.Add("to=" + Uri.EscapeDataString(to));
        return "?" + string.Join("&", parts);
    }

    public Task<ApiResult<HrReportSummaryDto>> SummaryAsync(string query, CancellationToken ct = default) =>
        GetPrivateAsync<HrReportSummaryDto>(Root + "/summary" + query, "HR recruitment report summary", ct);
    public Task<ApiResult<HrReportPageDto>> RowsAsync(string query, CancellationToken ct = default) =>
        GetPrivateAsync<HrReportPageDto>(Root + "/rows" + query, "HR recruitment report rows", ct);
    public Task<ApiResult<HrReportPreviewDto>> PreviewAsync(string query, CancellationToken ct = default) =>
        GetPrivateAsync<HrReportPreviewDto>(Root + "/preview" + query, "HR recruitment report preview", ct);
    public Task<ApiResult<DownloadedFile>> ExportAsync(string query, CancellationToken ct = default) =>
        GetFileAsync(Root + "/export" + query, ct);
    public Task<ApiResult<DownloadedFile>> ExportPdfAsync(string query, CancellationToken ct = default) =>
        GetFileAsync(Root + "/export/pdf" + query, ct);
}
