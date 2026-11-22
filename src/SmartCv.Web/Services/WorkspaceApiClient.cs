using SmartCv.Core.Workspaces;

namespace SmartCv.Web.Services;

/// <summary>B55/B56: workspace hiện tại theo JWT + workspace scope; không nhận workspaceId từ người dùng.</summary>
public sealed class WorkspaceApiClient(HttpClient http, ILogger<WorkspaceApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<WorkspaceDto>> GetWorkspaceAsync(CancellationToken ct = default) =>
        GetAsync<WorkspaceDto>("api/workspace/", ct);
}
