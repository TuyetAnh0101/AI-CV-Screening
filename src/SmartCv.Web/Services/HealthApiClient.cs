using System.Net;
using System.Net.Http.Json;
using SmartCv.Core.Ops;

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

    /// <summary>A44 supplies a valid dependency report with HTTP 503 as well as 200. This client remains anonymous.</summary>
    public async Task<ApiResult<HealthReportDto>> GetReadinessAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/health");
            using var response = await SendAuthorizedAsync(request, ct);
            if (response?.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
                return ApiResult<HealthReportDto>.Failure("Chưa kiểm tra được trạng thái máy chủ.", (int?)response?.StatusCode);
            var report = await response.Content.ReadFromJsonAsync<HealthReportDto>(ct);
            if (report?.Checks is null || report.Checks.Count != 3 || report.Checks.Any(c => c is null)
                || !report.Checks.Select(c => c.Name).ToHashSet().SetEquals(["database", "uploads", "ocr"])
                || report.Status != (report.Checks.All(c => c.Ok) ? "ok" : "error")
                || (response.StatusCode == HttpStatusCode.OK) != (report.Status == "ok"))
                return ApiResult<HealthReportDto>.LocalFailure("Máy chủ chưa cung cấp thông tin kiểm tra hợp lệ.", ApiFailureKind.InvalidData);
            return ApiResult<HealthReportDto>.Success(report);
        }
        catch (HttpRequestException) { return ApiResult<HealthReportDto>.LocalFailure("Chưa kết nối được máy chủ để kiểm tra.", ApiFailureKind.Network); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ApiResult<HealthReportDto>.LocalFailure("Kiểm tra máy chủ quá thời gian chờ.", ApiFailureKind.Timeout); }
        catch (System.Text.Json.JsonException) { return ApiResult<HealthReportDto>.LocalFailure("Thông tin kiểm tra máy chủ không đúng định dạng.", ApiFailureKind.InvalidData); }
    }
}
