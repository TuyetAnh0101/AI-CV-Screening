using SmartCv.Core.Contracts;
using SmartCv.Web.Models;

namespace SmartCv.Web.Services;

/// <summary>Gán CV vào Job qua A8. Không tạo lượt chấm hoặc gọi phân tích AI.</summary>
public sealed class ApplicationApiClient(HttpClient http, ILogger<ApplicationApiClient> logger)
    : ApiClientBase(http, logger)
{
    public Task<ApiResult<StageOptionsResponse>> GetStageOptionsAsync(CancellationToken ct = default) =>
        GetAsync<StageOptionsResponse>("api/applications/stage-options", ct);

    public Task<ApiResult<StageChangeResultDto>> ChangeStageAsync(int applicationId, ChangeStageRequest request, CancellationToken ct = default) =>
        PatchJsonAsync<StageChangeResultDto>($"api/applications/{applicationId}/stage", request, ct);

    public Task<ApiResult<BulkStageChangeResponse>> ChangeStagesAsync(BulkChangeStageRequest request, CancellationToken ct = default) =>
        PostJsonAsync<BulkStageChangeResponse>("api/applications/stage", request, ct);

    /// <summary>Chuẩn bị và gửi các CV đã tải thành công, có tích chọn, vào một Job đang tuyển.
    /// Chưa gọi từ nút Phân tích trên trang Upload; luồng điều phối nút đó thuộc B6.</summary>
    public Task<ApiResult<CreateApplicationsResponse>> AssignSelectedAsync(
        JobDto? job, IEnumerable<UploadItem> items, CancellationToken ct = default)
    {
        if (job is null || job.JobId <= 0 || !job.IsActive)
            return Task.FromResult(ApiResult<CreateApplicationsResponse>.Failure("Hãy chọn công việc đang tuyển."));

        var ids = items.Where(i => i.Selected && i.CanAnalyze && i.CvId != Guid.Empty)
            .Select(i => i.CvId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return Task.FromResult(ApiResult<CreateApplicationsResponse>.Failure("Hãy chọn ít nhất một CV đã tải lên thành công."));

        // A8 nhận tối đa 100 CV mỗi request; không gửi một phần lựa chọn một cách âm thầm.
        if (ids.Count > 100)
            return Task.FromResult(ApiResult<CreateApplicationsResponse>.Failure("Mỗi lần chỉ được gắn tối đa 100 CV."));

        return PostJsonAsync<CreateApplicationsResponse>("api/applications",
            new CreateApplicationsRequest { JobId = job.JobId, CvIds = ids }, ct);
    }
}
