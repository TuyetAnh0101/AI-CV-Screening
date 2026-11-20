using System.Globalization;
using SmartCv.Core.Contracts;
using SmartCv.Core.Export;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Services;

/// <summary>B30 reads A31's existing, Admin-only journal. Ranking download UI remains a separate workflow.</summary>
public sealed class ExportApiClient(HttpClient http, ILogger<ExportApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<ExportLogPageDto>> ListAsync(DateOnly from, DateOnly to, int page = 1, CancellationToken ct = default) =>
        GetPrivateAsync<ExportLogPageDto>($"api/exports?from={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&to={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}&pageSize=5", "Export journal", ct);

    public Task<ApiResult<ExportSettingsDto>> SettingsAsync(CancellationToken ct = default) =>
        GetPrivateAsync<ExportSettingsDto>("api/exports/settings", "Export settings", ct);
    public Task<ApiResult<RankingExportPreviewDto>> PreviewAsync(int runId, RankingExportQuery query, CancellationToken ct = default) =>
        GetPrivateAsync<RankingExportPreviewDto>($"api/runs/{runId}/ranking/export/preview" + RankingExportContext.QueryString(query), "Ranking export preview", ct);
    public Task<ApiResult<DownloadedFile>> DownloadAsync(int runId, RankingExportQuery query, CancellationToken ct = default) =>
        GetFileAsync($"api/runs/{runId}/ranking/export" + RankingExportContext.QueryString(query), ct);
}
