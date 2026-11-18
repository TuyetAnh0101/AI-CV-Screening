using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B26: cancellable /runs loads. A newer filter cancels the older request and a generation guard drops any late
/// response, so data of a previous filter is never presented as the current one.
/// </summary>
public sealed class RunHistoryWorkspace(RunApiClient api) : IDisposable
{
    public RunPageDto? Data { get; private set; }
    /// <summary>Filter that produced <see cref="Data"/>.</summary>
    public RunHistoryFilter? DataFilter { get; private set; }
    /// <summary>Filter currently requested (null while the URL filter is invalid).</summary>
    public RunHistoryFilter? Filter { get; private set; }
    public bool Loading { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Data belongs to the filter on screen.</summary>
    public bool ShowData => Data is not null && Filter is not null && DataFilter == Filter;

    /// <summary>Previous rows kept (dimmed) only while the next filter is still loading, never after an error.</summary>
    public bool ShowStale => Loading && Error is null && Data is not null && Filter is not null && DataFilter != Filter;

    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? request;
    private int generation;
    private bool disposed;

    /// <summary>Invalid URL filter: stop the pending request and show nothing for it.</summary>
    public void Invalidate()
    {
        ++generation; request?.Cancel(); Filter = null; Loading = false; Error = null;
    }

    public async Task LoadAsync(RunHistoryFilter filter)
    {
        if (disposed) return;
        request?.Cancel(); request?.Dispose();
        request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = request.Token;
        var current = ++generation;
        Filter = filter; Loading = true; Error = null;
        try
        {
            var result = await api.ListPageAsync(filter, ct);
            if (disposed || current != generation) return;
            if (!result.IsSuccess) { Error = Message(result.StatusCode, result.ErrorMessage); return; }
            var data = result.Data!;
            if (!Matches(data, filter))
            {
                Error = "Dữ liệu trả về không khớp bộ lọc hoặc trang đang chọn. Hãy làm mới để tải lại đúng phạm vi.";
                return;
            }
            Data = data; DataFilter = filter;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (!disposed && current == generation) Loading = false; }
    }

    /// <summary>
    /// Fixed sentences only: API details may carry technical text. Status null = no HTTP response
    /// (offline, timeout or unreadable JSON); ApiClientBase already maps those to fixed Vietnamese sentences.
    /// </summary>
    public static string Message(int? status, string? clientMessage, string subject = "lịch sử lượt chấm") => status switch
    {
        400 => "Máy chủ không chấp nhận bộ lọc này. Kiểm tra loại lượt, trạng thái, khoảng ngày và số trang rồi thử lại.",
        404 => $"Không tìm thấy API {subject}. Kiểm tra API đã cập nhật (A25) và ApiBaseUrl đúng cổng.",
        429 => "Đang có nhiều yêu cầu. Vui lòng chờ một lát rồi thử lại.",
        503 => "Máy chủ API tạm thời không sẵn sàng. Vui lòng thử lại sau ít phút.",
        >= 500 => $"Máy chủ API gặp lỗi khi tải {subject}. Xem log của API rồi thử lại.",
        null => clientMessage ?? "Không kết nối được máy chủ API. Hãy thử lại.",
        _ => $"Chưa tải được {subject} (mã {status})."
    };

    private static bool Matches(RunPageDto data, RunHistoryFilter filter) =>
        data.Items is not null && data.Page == filter.Page && data.PageSize == RunHistoryFilter.PageSize
        && data.TotalCount >= 0 && data.TotalPages >= 0 && data.Items.Count <= RunHistoryFilter.PageSize
        && data.Items.All(r => r is not null && r.Jobs is not null
            && (filter.Type is null || r.RunType == filter.Type) && (filter.Status is null || r.Status == filter.Status));

    public void Dispose()
    {
        disposed = true; ++generation; lifetime.Cancel(); request?.Cancel(); request?.Dispose(); lifetime.Dispose();
    }
}
