using SmartCv.Core.Contracts;
using SmartCv.Core.Cvs;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

public sealed class CvLibraryWorkspace(CvApiClient api) : IDisposable
{
    public CvPageDto? Data { get; private set; }
    public CvLibraryFilter? Filter { get; private set; }
    public bool Loading { get; private set; }
    public string? Error { get; private set; }
    public bool PageOutOfRange => Data is { TotalCount: > 0 } p && p.Page > p.TotalPages;
    private CancellationTokenSource? pending;
    private int generation;
    private bool disposed;

    public void Invalidate()
    {
        ++generation; pending?.Cancel(); Data = null; Loading = false; Error = null;
    }
    public async Task LoadAsync(CvLibraryFilter filter)
    {
        if (disposed) return;
        Invalidate();
        var (valid, validation) = filter.Validate();
        Filter = valid;
        if (valid is null) { Error = validation; return; }
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        pending = request;
        var version = generation;
        Loading = true;
        try
        {
            var result = await api.ListAsync(valid, request.Token);
            if (disposed || version != generation) return;
            if (!result.IsSuccess) { Error = CvLibraryText.Error(result.StatusCode); return; }
            var data = result.Data!;
            if (data.Items is null || data.Page != valid.Page || data.PageSize != valid.PageSize || data.TotalCount < 0
                || data.TotalPages != CvListQuery.TotalPages(data.TotalCount, valid.PageSize) || data.Items.Count > valid.PageSize)
            { Error = "Dữ liệu trả về không khớp trang đang chọn. Hãy làm mới để tải lại."; return; }
            Data = data;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        { if (!disposed && version == generation) Error = CvLibraryText.Error(null); }
        finally
        {
            if (ReferenceEquals(pending, request)) pending = null;
            if (!disposed && version == generation) Loading = false;
        }
    }
    public void Dispose() { disposed = true; Invalidate(); }
}
