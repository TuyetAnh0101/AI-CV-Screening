namespace SmartCv.Web.Services;

/// <summary>Kiểm tra API và các phụ thuộc sẵn sàng (GET /api/health); giữ hợp đồng status/time của huy hiệu.</summary>
public sealed class HealthApiClient(HttpClient http, ILogger<HealthApiClient> logger) : ApiClientBase(http, logger)
{
    public async Task<bool> IsApiOnlineAsync(CancellationToken ct = default)
    {
        var result = await GetAsync<HealthResponse>("api/health", ct);
        return result.IsSuccess && result.Data?.Status == "ok";
    }

    private sealed record HealthResponse(string Status, DateTime Time);
}
