using SmartCv.Core.Contracts;
using SmartCv.Web.Helpers;

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
    public Task<ApiResult<List<PromptTestRunDto>>> GetTestsAsync(CancellationToken ct = default) => GetAsync<List<PromptTestRunDto>>("api/prompt-tests", ct);
    public Task<ApiResult<PromptTestRunDto>> StartTestAsync(StartPromptTestRequest request, CancellationToken ct = default) => PostJsonAsync<PromptTestRunDto>("api/prompt-tests", request, ct);
    public Task<ApiResult<PromptTestProgressDto>> GetTestAsync(int id, CancellationToken ct = default) => GetAsync<PromptTestProgressDto>($"api/prompt-tests/{id}", ct);
    public Task<ApiResult<EvaluationSetDto>> CreateEvaluationSetAsync(CreateEvaluationSetRequest request, CancellationToken ct = default) => PostJsonAsync<EvaluationSetDto>("api/evaluation-sets", request, ct);
    public Task<ApiResult<EvaluationSetDetailDto>> GetEvaluationSetAsync(int id, CancellationToken ct = default) => GetAsync<EvaluationSetDetailDto>($"api/evaluation-sets/{id}", ct);
    public Task<ApiResult<List<LabelCandidateDto>>> GetLabelCandidatesAsync(int id, CancellationToken ct = default) => GetAsync<List<LabelCandidateDto>>($"api/evaluation-sets/{id}/applications", ct);
    public Task<ApiResult<SaveHrLabelsResult>> SaveLabelsAsync(int id, SaveHrLabelsRequest request, CancellationToken ct = default) => PutJsonAsync<SaveHrLabelsResult>($"api/evaluation-sets/{id}/labels", request, ct);
    /// <summary>B41 (A36): đề xuất nhãn từ quyết định HR. Chỉ đọc; nhãn chỉ được ghi qua SaveLabelsAsync khi HR bấm Lưu.</summary>
    public Task<ApiResult<LabelSuggestionResponse>> GetLabelSuggestionsAsync(int id, int? jobId = null, CancellationToken ct = default) =>
        GetAsync<LabelSuggestionResponse>(jobId is > 0 ? $"api/evaluation-sets/{id}/label-suggestions?jobId={jobId}" : $"api/evaluation-sets/{id}/label-suggestions", ct);

    // ----- SCApp v2: thành viên bộ CV (tách khỏi nhãn HR) -----

    /// <summary>Hồ sơ có thể thêm vào bộ: lọc theo Job, giai đoạn, ngày ứng tuyển (lịch Việt Nam) và chỉ hồ sơ chưa thuộc bộ.</summary>
    public Task<ApiResult<List<LabelCandidateDto>>> GetSetCandidatesAsync(int id, CandidatePickerFilter filter, CancellationToken ct = default) =>
        GetAsync<List<LabelCandidateDto>>($"api/evaluation-sets/{id}/applications{filter.Query}", ct);

    public Task<ApiResult<AddEvaluationSetMembersResult>> AddMembersAsync(int id, AddEvaluationSetMembersRequest request, CancellationToken ct = default) =>
        PostJsonAsync<AddEvaluationSetMembersResult>($"api/evaluation-sets/{id}/members", request, ct);

    public Task<ApiResult<RemoveEvaluationSetMemberResult>> RemoveMemberAsync(int id, int applicationId, CancellationToken ct = default) =>
        DeleteJsonAsync<RemoveEvaluationSetMemberResult>($"api/evaluation-sets/{id}/members/{applicationId}", ct);

    /// <summary>Đưa một thành viên về "Chưa đánh giá" (bỏ nhãn, vẫn giữ trong bộ).</summary>
    public Task<ApiResult<bool>> ClearLabelAsync(int id, int applicationId, CancellationToken ct = default) =>
        DeleteAsync($"api/evaluation-sets/{id}/labels/{applicationId}", ct);
    public Task<ApiResult<List<PromptVersionDto>>> GetVersionsAsync(CancellationToken ct = default) =>
        GetAsync<List<PromptVersionDto>>("api/prompts", ct);

    public Task<ApiResult<PromptVersionDto>> CreateVersionAsync(CreatePromptVersionRequest request, CancellationToken ct = default) =>
        PostJsonAsync<PromptVersionDto>("api/prompts", request, ct);

    public Task<ApiResult<PromptVersionDto>> ActivateAsync(int promptVersionId, CancellationToken ct = default) =>
        PostJsonAsync<PromptVersionDto>($"api/prompts/{promptVersionId}/activate", new { }, ct);

    // ----- B39 (A41): "Thử với hồ sơ…". Hai API dưới KHÔNG gọi Gemini, KHÔNG tốn lượt, KHÔNG ghi database. -----

    /// <summary>GET /api/applications/ai-preview/quota: số lượt chấm thử còn lại (giới hạn và cửa sổ lấy từ API).</summary>
    public Task<ApiResult<PreviewQuotaDto>> GetPreviewQuotaAsync(CancellationToken ct = default) =>
        GetAsync<PreviewQuotaDto>("api/applications/ai-preview/quota", ct);

    /// <summary>
    /// POST /api/prompts/assembled-preview: nội dung SẼ gửi Gemini (CV đã che). Chỉ gửi đúng MỘT nguồn prompt;
    /// yêu cầu thiếu/thừa nguồn bị chặn tại đây, không gửi đi (không bao giờ rơi về "bản đang kích hoạt").
    /// </summary>
    public Task<ApiResult<AssembledPromptDto>> GetAssembledPromptAsync(AssembledPreviewRequest request, CancellationToken ct = default)
    {
        if (request.ApplicationId <= 0)
            return Task.FromResult(ApiResult<AssembledPromptDto>.Failure("Hãy chọn hồ sơ trước khi xem nội dung gửi Gemini."));
        object? body = PlaygroundRules.SingleSource(request.SystemPrompt, request.PromptVersionId) switch
        {
            { SystemPrompt: { } draft } => new { applicationId = request.ApplicationId, systemPrompt = draft },
            { PromptVersionId: { } id } => new { applicationId = request.ApplicationId, promptVersionId = id },
            _ => null
        };
        return body is null
            ? Task.FromResult(ApiResult<AssembledPromptDto>.Failure(PlaygroundRules.InvalidSourceMessage))
            : PostJsonAsync<AssembledPromptDto>("api/prompts/assembled-preview", body, ct);
    }
}
