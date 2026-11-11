using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>B12–B13: immutable versions and activation through the existing A14 API.</summary>
public sealed class PromptApiClient(HttpClient http, ILogger<PromptApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<List<PromptTestRunDto>>> GetMismatchRunsAsync(CancellationToken ct = default) =>
        GetAsync<List<PromptTestRunDto>>("api/prompt-tests", ct);
    public Task<ApiResult<PromptTestProgressDto>> GetMismatchRunAsync(int runId, CancellationToken ct = default) =>
        GetAsync<PromptTestProgressDto>($"api/prompt-tests/{runId}", ct);
    public Task<ApiResult<EvaluationDetailDto>> GetMismatchEvaluationAsync(int evaluationId, CancellationToken ct = default) =>
        GetAsync<EvaluationDetailDto>($"api/evaluations/{evaluationId}", ct);
    public Task<ApiResult<List<PromptTestRunDto>>> GetAccuracyRunsAsync(int setId, CancellationToken ct = default) =>
        GetAsync<List<PromptTestRunDto>>($"api/prompt-tests?evaluationSetId={setId}", ct);
    public Task<ApiResult<PromptCompareDto>> CompareAccuracyRunsAsync(int baseId, int candidateId, CancellationToken ct = default) =>
        GetAsync<PromptCompareDto>($"api/prompt-tests/compare?baseRunId={baseId}&candidateRunId={candidateId}", ct);
    public Task<ApiResult<List<EvaluationSetDto>>> GetEvaluationSetsAsync(CancellationToken ct = default) => GetAsync<List<EvaluationSetDto>>("api/evaluation-sets", ct);
    public Task<ApiResult<List<PromptTestSummaryDto>>> GetTestsAsync(CancellationToken ct = default) => GetAsync<List<PromptTestSummaryDto>>("api/prompt-tests", ct);
    public Task<ApiResult<PromptTestSummaryDto>> StartTestAsync(StartPromptTestRequest request, CancellationToken ct = default) => PostJsonAsync<PromptTestSummaryDto>("api/prompt-tests", request, ct);
    public Task<ApiResult<PromptTestDetailDto>> GetTestAsync(int id, CancellationToken ct = default) => GetAsync<PromptTestDetailDto>($"api/prompt-tests/{id}", ct);
    public Task<ApiResult<List<PromptVersionDto>>> GetVersionsAsync(CancellationToken ct = default) =>
        GetAsync<List<PromptVersionDto>>("api/prompts", ct);

    public Task<ApiResult<PromptVersionDto>> CreateVersionAsync(CreatePromptVersionRequest request, CancellationToken ct = default) =>
        PostJsonAsync<PromptVersionDto>("api/prompts", request, ct);

    public Task<ApiResult<PromptVersionDto>> ActivateAsync(int promptVersionId, CancellationToken ct = default) =>
        PostJsonAsync<PromptVersionDto>($"api/prompts/{promptVersionId}/activate", new { }, ct);
}
