using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>B20 state. A failed write is never replayed; only a successful read unlocks it.</summary>
public sealed class JobsWorkspace(JobApiClient api) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public IReadOnlyList<JobDto> Jobs { get; private set; } = [];
    public bool Loading { get; private set; } = true;
    public bool Busy { get; private set; }
    public bool NeedsVerification { get; private set; }
    public string? Error { get; private set; }
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }
    public JobDto? Pending { get; private set; }
    public string Show { get; set; } = "active";
    public string Search { get; set; } = "";
    public List<JobDto> Visible => Jobs.Where(j => Show == "all" || (Show == "active") == j.IsActive)
        .Where(j => string.IsNullOrWhiteSpace(Search) || j.Title.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase))
        .OrderByDescending(j => j.IsActive).ThenBy(j => j.JobId).ToList();

    public bool CanToggle(JobDto job) => !disposed && !Loading && !Busy && !NeedsVerification && Pending is null && job.HasEvaluations is not null;
    public void Ask(JobDto job) { if (CanToggle(job)) { Pending = job; Message = null; } }
    public void Cancel() { if (!Busy) Pending = null; }
    public void ClearFilters() { Show = "all"; Search = ""; }

    public async Task LoadAsync()
    {
        if (disposed || Busy || Pending is not null) return;
        Loading = true;
        Error = null;
        try
        {
            var response = await api.GetJobsAsync(activeOnly: false, lifetime.Token);
            if (disposed) return;
            if (response.IsSuccess)
            {
                Jobs = response.Data!;
                if (NeedsVerification) { Message = "Đã tải lại trạng thái hiện tại. Hãy kiểm tra vị trí trước khi thao tác tiếp."; MessageIsError = false; }
                NeedsVerification = false;
            }
            else Error = "Chưa tải được danh sách vị trí. Kiểm tra kết nối máy chủ và thử lại.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { Loading = false; }
    }

    public async Task ToggleAsync()
    {
        if (disposed || Busy || Loading || NeedsVerification || Pending is not { } job) return;
        Busy = true;
        try
        {
            var response = job.IsActive ? await api.DeactivateAsync(job.JobId, lifetime.Token) : await api.ActivateAsync(job.JobId, lifetime.Token);
            if (disposed) return;
            Pending = null;
            if (!response.IsSuccess || response.Data!.JobId != job.JobId || response.Data.IsActive == job.IsActive)
            {
                // Even a server error can arrive after a write was applied. Require explicit GET verification.
                NeedsVerification = true;
                MessageIsError = true;
                Message = response.StatusCode is 401 or 403
                    ? "Bạn chưa có quyền thay đổi vị trí. Tải lại trạng thái để kiểm tra quyền truy cập."
                    : response.StatusCode == 404 ? "Vị trí hoặc thao tác không còn khả dụng. Tải lại trạng thái để kiểm tra."
                    : "Chưa xác nhận được kết quả thay đổi. Tải lại trạng thái trước khi thao tác tiếp.";
                return;
            }
            Jobs = Jobs.Select(j => j.JobId == job.JobId ? response.Data : j).ToList();
            MessageIsError = false;
            Message = response.Data.IsActive ? $"Đã mở lại “{job.Title}”." : $"Đã ngừng tuyển “{job.Title}”. Vị trí không còn trong danh sách chọn khi tải CV.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { Busy = false; }
    }

    public void Dispose() { disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
}
