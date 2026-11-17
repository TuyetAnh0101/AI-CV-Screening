using SmartCv.Core.Contracts;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Services;

/// <summary>B19 reads the existing A19 summary; no secondary calculation or write endpoint.</summary>
public sealed class DashboardApiClient(HttpClient http, ILogger<DashboardApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<DashboardSummaryDto>> GetSummaryAsync(DashboardFilter filter, CancellationToken ct = default) =>
        GetAsync<DashboardSummaryDto>("api/dashboard/summary" + filter.Query, ct);
}
