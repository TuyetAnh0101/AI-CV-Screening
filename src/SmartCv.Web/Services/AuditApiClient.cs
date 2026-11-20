using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

public sealed class AuditApiClient(HttpClient http, ILogger<AuditApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<AuditPageDto>> ListAsync(DateOnly from, DateOnly to, string? search, string? category,
        string? action, string? actor, string? outcome, string? resourceType, string? resourceId, string? requestId,
        int page, int pageSize, CancellationToken ct = default)
    {
        var fields = new Dictionary<string, string?> { ["from"] = from.ToString("yyyy-MM-dd"), ["to"] = to.ToString("yyyy-MM-dd"),
            ["search"] = search, ["category"] = category, ["action"] = action, ["actorUserId"] = actor,
            ["outcome"] = outcome, ["resourceType"] = resourceType, ["resourceId"] = resourceId, ["requestId"] = requestId,
            ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture), ["pageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var query = string.Join('&', fields.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value!)));
        return GetPrivateAsync<AuditPageDto>("api/admin/audit-events?" + query, "Activity log", ct);
    }
    public Task<ApiResult<AuditEventDto>> GetAsync(long id, CancellationToken ct = default) =>
        GetPrivateAsync<AuditEventDto>($"api/admin/audit-events/{id}", "Activity event", ct);
}
