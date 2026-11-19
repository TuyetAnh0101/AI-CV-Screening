using SmartCv.Core.Contracts;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Services;

/// <summary>B19 reads the existing A19 summary; no secondary calculation or write endpoint.</summary>
public sealed class DashboardApiClient(HttpClient http, ILogger<DashboardApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<DashboardSummaryDto>> GetSummaryAsync(DashboardFilter filter, CancellationToken ct = default) =>
        GetAsync<DashboardSummaryDto>("api/dashboard/summary" + filter.Query, ct);

    /// <summary>SCApp v2: xu hướng hồ sơ mới và tỷ lệ qua vòng CV, cùng bộ lọc với phần tóm tắt.</summary>
    public Task<ApiResult<DashboardTrendDto>> GetTrendAsync(DashboardFilter filter, CancellationToken ct = default) =>
        GetAsync<DashboardTrendDto>("api/dashboard/trend" + filter.Query, ct);
}
