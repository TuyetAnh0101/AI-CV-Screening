using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>
/// Requests use the JWT for THIS Blazor circuit via ApiClientBase.UseSession.
/// Never call admin operations with a public HttpClient or put tokens into JavaScript.
/// </summary>
public sealed class AdminOperationsApiClient(HttpClient http, ILogger<AdminOperationsApiClient> logger)
    : ApiClientBase(http, logger)
{
    public Task<ApiResult<AdminSystemOverviewDto>> GetOverviewAsync(CancellationToken ct = default) =>
        GetAsync<AdminSystemOverviewDto>("api/admin/ops/overview", ct);

    public Task<ApiResult<AdminAiUsageDto>> GetAiUsageAsync(int days, CancellationToken ct = default) =>
        GetAsync<AdminAiUsageDto>($"api/admin/ops/ai-usage?days={days}", ct);

    /// <summary>Read historical run/evaluation aggregates (not Gemini request telemetry).</summary>
    public Task<ApiResult<AdminAiHistoryDto>> GetAiHistoryAsync(string period, CancellationToken ct = default) =>
        GetAsync<AdminAiHistoryDto>("api/admin/ops/ai-history?period=" + Uri.EscapeDataString(period), ct);

    public Task<ApiResult<AdminWorkspacePageDto>> GetWorkspacesAsync(int page, string? search,
        CancellationToken ct = default) =>
        GetAsync<AdminWorkspacePageDto>($"api/admin/ops/workspaces?page={page}&search={Uri.EscapeDataString(search ?? string.Empty)}", ct);

    public Task<ApiResult<AdminIntegrationSnapshotDto>> GetIntegrationsAsync(CancellationToken ct = default) =>
        GetAsync<AdminIntegrationSnapshotDto>("api/admin/ops/integrations", ct);

    /// <summary>Read existing seven-day usage and auth audit, no provider call.</summary>
    public Task<ApiResult<AdminProviderInsightsDto>> GetProviderInsightsAsync(CancellationToken ct = default) =>
        GetAsync<AdminProviderInsightsDto>("api/admin/ops/integrations/insights", ct);

    /// <summary>Explicit bounded server-side Gemini models.get probe; no secret reaches Blazor.</summary>
    public Task<ApiResult<AdminGeminiCheckDto>> CheckGeminiAsync(CancellationToken ct = default) =>
        PostJsonAsync<AdminGeminiCheckDto>("api/admin/ops/integrations/gemini/check", new { }, ct);

    public Task<ApiResult<AdminQuotaChangeResultDto>> ChangeQuotaAsync(int workspaceId,
        AdminQuotaChangeRequest request, CancellationToken ct = default) =>
        SendRequestAsync<AdminQuotaChangeResultDto>(
            () => JsonRequest(HttpMethod.Patch, $"api/admin/ops/workspaces/{workspaceId}/quota", request),
            "api/admin/ops/workspaces/{id}/quota", ct);
}
