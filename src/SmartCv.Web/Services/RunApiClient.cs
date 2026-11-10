using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>
/// B6: lượt phân tích (EvaluationRuns) qua API thật của A11–A13.
/// POST /api/runs { applicationIds } → 201 + RunProgressDto (trạng thái Queued);
/// GET /api/runs/{id} → RunProgressDto kèm Items (404 khi không có lượt).
/// </summary>
public sealed class RunApiClient(HttpClient http, ILogger<RunApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<RunProgressDto>> StartAsync(IReadOnlyCollection<int> applicationIds, CancellationToken ct = default) =>
        PostJsonAsync<RunProgressDto>("api/runs", new StartRunRequest { ApplicationIds = [.. applicationIds] }, ct);

    public Task<ApiResult<RunProgressDto>> GetRunAsync(int runId, CancellationToken ct = default) =>
        GetAsync<RunProgressDto>($"api/runs/{runId}", ct);
}
