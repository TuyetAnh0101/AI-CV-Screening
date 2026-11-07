using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>Gọi API job. Dùng ở trang chủ (B2) và ô chọn job (B4).</summary>
public sealed class JobApiClient(HttpClient http, ILogger<JobApiClient> logger) : ApiClientBase(http, logger)
{
    /// <summary>GET /api/jobs: danh sách job đang tuyển kèm kỹ năng.</summary>
    public Task<ApiResult<List<JobDto>>> GetJobsAsync(bool activeOnly = true, CancellationToken ct = default) =>
        GetAsync<List<JobDto>>($"api/jobs?activeOnly={activeOnly.ToString().ToLowerInvariant()}", ct);

    /// <summary>GET /api/jobs/{id}: chi tiết một job.</summary>
    public Task<ApiResult<JobDto>> GetJobAsync(int jobId, CancellationToken ct = default) =>
        GetAsync<JobDto>($"api/jobs/{jobId}", ct);
}
