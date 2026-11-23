using SmartCv.Core.Workspaces;

namespace SmartCv.Web.Services;

/// <summary>B55/B56: workspace hiện tại theo JWT + workspace scope; không nhận workspaceId từ người dùng.</summary>
public sealed class WorkspaceApiClient(HttpClient http, ILogger<WorkspaceApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<WorkspaceDto>> GetWorkspaceAsync(CancellationToken ct = default) =>
        GetAsync<WorkspaceDto>("api/workspace/", ct);

    /// <summary>A54: idempotent for duplicate sample jobs; a 409 means check the job list, do not retry blindly.</summary>
    public Task<ApiResult<SmartCv.Core.Contracts.JobDto>> CreateSampleJobAsync(CancellationToken ct = default) =>
        SendRequestAsync<SmartCv.Core.Contracts.JobDto>(
            () => new HttpRequestMessage(HttpMethod.Post, "api/workspace/sample-job"),
            "api/workspace/sample-job", ct);
}
