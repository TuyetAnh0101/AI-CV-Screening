using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>
/// Lượt phân tích (EvaluationRuns) qua API thật của A11–A13.
/// B6: POST /api/runs { applicationIds } → 201 + RunProgressDto (trạng thái Queued);
///     GET /api/runs/{id} → RunProgressDto kèm Items (404 khi không có lượt).
/// B7: GET /api/runs?type=Screening, GET /api/runs/latest, GET /api/runs/{id}/ranking?jobId= (A13).
/// </summary>
public sealed class RunApiClient(HttpClient http, ILogger<RunApiClient> logger) : ApiClientBase(http, logger)
{
    /// <summary>Số lượt chấm tối đa A13 trả về một lần (API tự giới hạn 1–100).</summary>
    public const int MaxRunsPerList = 100;

    public Task<ApiResult<RunProgressDto>> StartAsync(IReadOnlyCollection<int> applicationIds, CancellationToken ct = default) =>
        PostJsonAsync<RunProgressDto>("api/runs", new StartRunRequest { ApplicationIds = [.. applicationIds] }, ct);

    public Task<ApiResult<RunProgressDto>> GetRunAsync(int runId, CancellationToken ct = default) =>
        GetAsync<RunProgressDto>($"api/runs/{runId}", ct);

    /// <summary>B7: danh sách lượt chấm Screening, mới nhất trước (ô chọn "Lượt chấm").</summary>
    public Task<ApiResult<List<RunSummaryDto>>> ListScreeningAsync(int take = 50, CancellationToken ct = default) =>
        GetAsync<List<RunSummaryDto>>(
            $"api/runs?type={RunTypes.Screening}&take={Math.Clamp(take, 1, MaxRunsPerList)}", ct);

    /// <summary>B7: lượt chấm Screening mới nhất (404 = chưa có lượt nào). Có jobId: lượt mới nhất chứa job đó.</summary>
    public Task<ApiResult<RunSummaryDto>> GetLatestAsync(int? jobId = null, CancellationToken ct = default) =>
        GetAsync<RunSummaryDto>(jobId is null ? "api/runs/latest" : $"api/runs/latest?jobId={jobId}", ct);

    /// <summary>
    /// B7: bảng xếp hạng một job trong một lượt chấm. Không truyền jobId: API lấy job đầu tiên của lượt.
    /// 404: lượt chấm không tồn tại, lượt không có hồ sơ, hoặc job không thuộc lượt chấm.
    /// </summary>
    public Task<ApiResult<RankingResponse>> GetRankingAsync(int runId, int? jobId = null, CancellationToken ct = default) =>
        GetAsync<RankingResponse>(jobId is null ? $"api/runs/{runId}/ranking" : $"api/runs/{runId}/ranking?jobId={jobId}", ct);
}
