using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>B12: immutable prompt versions through the existing A14 API.</summary>
public sealed class PromptApiClient(HttpClient http, ILogger<PromptApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<List<PromptVersionDto>>> GetVersionsAsync(CancellationToken ct = default) =>
        GetAsync<List<PromptVersionDto>>("api/prompts", ct);

    public Task<ApiResult<PromptVersionDto>> CreateVersionAsync(CreatePromptVersionRequest request, CancellationToken ct = default) =>
        PostJsonAsync<PromptVersionDto>("api/prompts", request, ct);
}
