using SmartCv.Core.Contracts;
using SmartCv.Core.Cvs;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B24: tải danh sách NGƯỜI. Mỗi lần tải có số thế hệ + CancellationToken (30 giây): phản hồi cũ về muộn bị bỏ,
/// request cũ bị hủy khi đổi từ khóa / trang / chế độ. Không có thao tác ghi ở đây.
/// </summary>
public sealed class CandidateListWorkspace(CandidateApiClient api) : IDisposable
{
    public CandidatePageDto? Data { get; private set; }
    public CandidateListFilter? Filter { get; private set; }
    public bool Loading { get; private set; }
    public string? Error { get; private set; }
    public bool PageOutOfRange => Data is { TotalCount: > 0 } p && p.Page > p.TotalPages;
    private CancellationTokenSource? pending;
    private int generation;
    private bool disposed;

    public void Invalidate() { ++generation; pending?.Cancel(); Loading = false; Error = null; }

    public async Task LoadAsync(CandidateListFilter filter)
    {
        if (disposed) return;
        Invalidate();
        var (valid, validation) = filter.Validate();
        Filter = valid;
        if (valid is null) { Data = null; Error = validation; return; }
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        pending = request;
        var version = generation;
        Loading = true;
        try
        {
            var result = await api.ListPeopleAsync(valid, request.Token);
            if (disposed || version != generation) return;
            if (!result.IsSuccess) { Error = CandidateText.Error(result.StatusCode); return; }
            var data = result.Data!;
            if (data.Items is null || data.Page != valid.Page || data.PageSize != valid.PageSize || data.TotalCount < 0
                || data.TotalPages != CvListQuery.TotalPages(data.TotalCount, valid.PageSize) || data.Items.Count > valid.PageSize)
            { Error = "Dữ liệu trả về không khớp trang đang chọn. Hãy làm mới để tải lại."; return; }
            Data = data;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        { if (!disposed && version == generation) Error = CandidateText.Error(null); }
        finally
        {
            if (ReferenceEquals(pending, request)) pending = null;
            if (!disposed && version == generation) Loading = false;
        }
    }

    public void Dispose() { disposed = true; Invalidate(); }
}

/// <summary>
/// B24: trang chi tiết một người. Đọc GET /api/candidates/{id}; nếu 404 thì đọc nhật ký A43 để phân biệt
/// "đã xóa theo yêu cầu" (vẫn phải hiện nhật ký làm bằng chứng) với "không tồn tại / đã được gộp".
/// </summary>
public sealed class CandidateDetailWorkspace(CandidateApiClient api) : IDisposable
{
    public enum State { Loading, Ready, Erased, NotFound, Error }
    public State Status { get; private set; } = State.Loading;
    public int CandidateId { get; private set; }
    public CandidateDetailDto? Data { get; private set; }
    public List<DataRequestDto> Requests { get; private set; } = [];
    public string? Error { get; private set; }
    public bool Refreshing { get; private set; }
    private CancellationTokenSource? pending;
    private int generation;
    private bool disposed;

    /// <summary>Tải lại sau thao tác ghi: giữ dữ liệu cũ trên màn hình tới khi có dữ liệu mới (không nháy trắng).</summary>
    public async Task LoadAsync(int id, bool keepVisible = false)
    {
        if (disposed) return;
        ++generation; pending?.Cancel();
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        pending = request;
        var version = generation;
        if (id != CandidateId || !keepVisible) { Data = null; Requests = []; Status = State.Loading; }
        CandidateId = id; Error = null; Refreshing = keepVisible && Data is not null;
        try
        {
            var result = await api.GetAsync(id, request.Token);
            if (disposed || version != generation) return;
            if (result.IsSuccess)
            {
                if (result.Data!.CandidateId != id) { Fail("Dữ liệu trả về không đúng ứng viên đang mở. Hãy tải lại."); return; }
                Data = result.Data; Requests = result.Data.DataRequests; Status = State.Ready; return;
            }
            if (result.StatusCode is 404 or 410)
            {
                var log = await api.ListRequestsAsync(id, request.Token);
                if (disposed || version != generation) return;
                Data = null;
                Requests = log.IsSuccess ? log.Data!.OrderByDescending(r => r.CreatedAt).ToList() : [];
                Status = Requests.Any(r => r.RequestType == DataRequestTypes.Erase) ? State.Erased : State.NotFound;
                return;
            }
            Fail(CandidateText.Error(result.StatusCode));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        { if (!disposed && version == generation) Fail(CandidateText.Error(null)); }
        finally
        {
            if (ReferenceEquals(pending, request)) pending = null;
            if (!disposed && version == generation) Refreshing = false;
        }
    }

    private void Fail(string message) { Error = message; if (Data is null) Status = State.Error; }

    /// <summary>Sau khi xóa dữ liệu thành công: chuyển sang trạng thái "đã xóa" mà không cần đoán.</summary>
    public void MarkErased() { Data = null; Status = State.Erased; }

    public void Dispose() { disposed = true; ++generation; pending?.Cancel(); }
}
