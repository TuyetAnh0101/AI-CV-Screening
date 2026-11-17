using SmartCv.Core.Contracts;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Services;

/// <summary>
/// B39 (A41): POST /api/applications/{id}/ai-preview — chấm THỬ một hồ sơ bằng prompt nháp hoặc phiên bản đã lưu, KHÔNG lưu.
/// Tách khỏi <see cref="PromptApiClient"/> chỉ vì thời gian chờ: một lần chấm thử có thể chờ Gemini thử lại
/// (Gemini:TimeoutSeconds 90 giây/lần, có khoảng chờ giữa các lần), lâu hơn giới hạn 30 giây của các API prompt khác.
/// Không bao giờ gọi POST /api/prompts hay /activate; không tự thử lại vì mỗi lần gọi có thể tốn một lượt Gemini.
/// </summary>
public sealed class AiPreviewApiClient(HttpClient http, ILogger<AiPreviewApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<AiPreviewDto>> PreviewAsync(int applicationId, AiPreviewRequest request, CancellationToken ct = default)
    {
        if (applicationId <= 0)
            return Task.FromResult(ApiResult<AiPreviewDto>.Failure("Hãy chọn hồ sơ trước khi chấm thử."));

        // Body chỉ chứa ĐÚNG một nguồn: không gửi cả hai, không gửi body rỗng (rỗng = bản đang kích hoạt của A10).
        object? body = PlaygroundRules.SingleSource(request.SystemPrompt, request.PromptVersionId) switch
        {
            { SystemPrompt: { } draft } => new { systemPrompt = draft },
            { PromptVersionId: { } id } => new { promptVersionId = id },
            _ => null
        };
        return body is null
            ? Task.FromResult(ApiResult<AiPreviewDto>.Failure(PlaygroundRules.InvalidSourceMessage))
            : PostJsonAsync<AiPreviewDto>($"api/applications/{applicationId}/ai-preview", body, ct);
    }
}
