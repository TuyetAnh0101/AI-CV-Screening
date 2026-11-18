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

    public Task<ApiResult<JobDto>> DeactivateAsync(int jobId, CancellationToken ct = default) =>
        PostJsonAsync<JobDto>($"api/jobs/{jobId}/deactivate", new { }, ct);

    public Task<ApiResult<JobDto>> ActivateAsync(int jobId, CancellationToken ct = default) =>
        PostJsonAsync<JobDto>($"api/jobs/{jobId}/activate", new { }, ct);

    public Task<ApiResult<JobDto>> CreateAsync(JobUpsertRequest request, CancellationToken ct = default) => PostJsonAsync<JobDto>("api/jobs", request, ct);
    public Task<ApiResult<JobDto>> UpdateAsync(int jobId, JobUpsertRequest request, CancellationToken ct = default) => PutJsonAsync<JobDto>($"api/jobs/{jobId}", request, ct);
    public Task<ApiResult<JobSkillDto>> AddSkillAsync(int jobId, JobSkillInput request, CancellationToken ct = default) => PostJsonAsync<JobSkillDto>($"api/jobs/{jobId}/skills", request, ct);
    public Task<ApiResult<JobSkillDto>> UpdateSkillAsync(int jobId, string code, UpdateJobSkillRequest request, CancellationToken ct = default) => PutJsonAsync<JobSkillDto>($"api/jobs/{jobId}/skills/{Uri.EscapeDataString(code)}", request, ct);
    public Task<ApiResult<bool>> DeleteSkillAsync(int jobId, string code, CancellationToken ct = default) => DeleteAsync($"api/jobs/{jobId}/skills/{Uri.EscapeDataString(code)}", ct);
    public Task<ApiResult<JobSkillDto>> ReplaceAliasesAsync(int jobId, string code, IEnumerable<string> aliases, CancellationToken ct = default) => PutJsonAsync<JobSkillDto>($"api/jobs/{jobId}/skills/{Uri.EscapeDataString(code)}/aliases", new ReplaceAliasesRequest { Aliases = aliases.Select(a => (string?)a).ToList() }, ct);
}
