using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>Independent job lookup and cancellable summary loads. Previous valid data survives refresh failures.</summary>
public sealed class DashboardWorkspace(DashboardApiClient api, JobApiClient jobApi) : IDisposable
{
    public List<JobDto> Jobs { get; private set; } = [];
    public bool JobsLoading { get; private set; }
    public bool JobsLoaded { get; private set; }
    public string? JobsError { get; private set; }
    public DashboardSummaryDto? Data { get; private set; }
    public DashboardFilter? DataFilter { get; private set; }
    public DashboardFilter? Filter { get; private set; }
    public bool Loading { get; private set; }
    public string? Error { get; private set; }
    public bool ShowData => Data is not null && DataFilter == Filter && Filter is not null;
    // SCApp v2: xu hướng tải song song, cùng bộ lọc; lỗi xu hướng không chặn phần tóm tắt.
    public DashboardTrendDto? Trend { get; private set; }
    public DashboardFilter? TrendFilter { get; private set; }
    public string? TrendError { get; private set; }
    public bool TrendLoading { get; private set; }
    public DashboardTrendDto? ShownTrend => TrendFilter == Filter && Filter is not null ? Trend : null;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? request;
    private int generation;
    private bool disposed;

    public async Task LoadJobsAsync()
    {
        if (disposed || JobsLoading) return;
        JobsLoading = true; JobsError = null;
        try
        {
            var result = await jobApi.GetJobsAsync(activeOnly: false, lifetime.Token);
            if (disposed) return;
            if (result.IsSuccess) { Jobs = result.Data!; JobsLoaded = true; }
            else JobsError = "Chưa tải được danh sách vị trí. Bộ lọc trên địa chỉ vẫn được giữ; hãy thử tải lại danh sách.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { if (!disposed) JobsLoading = false; }
    }

    public void Invalidate()
    {
        ++generation; request?.Cancel(); Filter = null; Loading = false; Error = null;
    }

    public async Task LoadAsync(DashboardFilter filter)
    {
        if (disposed) return;
        request?.Cancel(); request?.Dispose(); request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = request.Token; var current = ++generation;
        Filter = filter; Loading = true; Error = null;
        TrendLoading = true; TrendError = null;
        var trendTask = LoadTrendAsync(filter, current, ct);
        try
        {
            var result = await api.GetSummaryAsync(filter, ct);
            if (disposed || current != generation) return;
            if (!result.IsSuccess)
            {
                Error = result.StatusCode switch
                {
                    400 => "Bộ lọc chưa được máy chủ chấp nhận. Kiểm tra vị trí và khoảng ngày rồi thử lại.",
                    404 => "Không tìm thấy vị trí hoặc API Tổng quan chưa được bật. Chọn lại vị trí hoặc kiểm tra kết nối API.",
                    429 => "Đang có nhiều yêu cầu. Vui lòng chờ một lát rồi làm mới Tổng quan.",
                    null => "Không kết nối được API hoặc máy chủ phản hồi quá thời gian chờ. Bộ lọc được giữ để bạn thử lại.",
                    _ => "Chưa tải được số liệu Tổng quan từ máy chủ. Hãy thử lại sau."
                };
                return;
            }
            var data = result.Data!;
            if (data.JobId != filter.JobId || data.From != filter.From || data.To != filter.To)
            { Error = "Số liệu trả về không khớp bộ lọc đang chọn. Hãy làm mới để lấy đúng phạm vi."; return; }
            Data = data; DataFilter = filter;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (!disposed && current == generation) Loading = false; }
        await trendTask;
    }

    private async Task LoadTrendAsync(DashboardFilter filter, int current, CancellationToken ct)
    {
        try
        {
            var result = await api.GetTrendAsync(filter, ct);
            if (disposed || current != generation) return;
            if (!result.IsSuccess)
            {
                TrendError = result.StatusCode == 400 ? result.ErrorMessage ?? "Khoảng ngày chưa hợp lệ cho biểu đồ." : "Chưa tải được dữ liệu xu hướng. Hãy làm mới để thử lại.";
                return;
            }
            var data = result.Data!;
            if (data.JobId != filter.JobId || (filter.From is { } f && data.From != f) || (filter.To is { } t && data.To != t))
            { TrendError = "Số liệu xu hướng không khớp bộ lọc đang chọn. Hãy làm mới."; return; }
            Trend = data; TrendFilter = filter;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (!disposed && current == generation) TrendLoading = false; }
    }

    public void Dispose()
    { disposed = true; ++generation; lifetime.Cancel(); request?.Cancel(); request?.Dispose(); lifetime.Dispose(); }
}
