using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>B12–B13: immutable versions and activation through the existing A14 API.</summary>
public sealed class PromptApiClient(HttpClient http, ILogger<PromptApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<List<PromptVersionDto>>> GetVersionsAsync(CancellationToken ct = default) =>
        GetAsync<List<PromptVersionDto>>("api/prompts", ct);

    public Task<ApiResult<PromptVersionDto>> CreateVersionAsync(CreatePromptVersionRequest request, CancellationToken ct = default) =>
        PostJsonAsync<PromptVersionDto>("api/prompts", request, ct);

    public Task<ApiResult<PromptVersionDto>> ActivateAsync(int promptVersionId, CancellationToken ct = default) =>
        PostJsonAsync<PromptVersionDto>($"api/prompts/{promptVersionId}/activate", new { }, ct);
}
