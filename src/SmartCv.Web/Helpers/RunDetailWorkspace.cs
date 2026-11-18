using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B26: /runs/{id}. Summary (GET /api/runs/{id}) and failures (GET /api/runs/{id}/failures) load independently:
/// a failures error keeps the summary and can be retried alone. Changing the route id clears the previous run at once,
/// so run #A is never shown under the heading of run #B.
/// </summary>
public sealed class RunDetailWorkspace(RunApiClient api) : IDisposable
{
    public int RunId { get; private set; }
    public RunProgressDto? Run { get; private set; }
    public bool Loading { get; private set; }
    public string? Error { get; private set; }
    public bool NotFound { get; private set; }

    public RunFailuresDto? Failures { get; private set; }
    public bool FailuresLoading { get; private set; }
    public string? FailuresError { get; private set; }

    /// <summary>Raised when the summary is ready but failures are still loading, so the page can render it early.</summary>
    public event Action? Changed;

    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? request, failuresRequest;
    private int generation, failuresGeneration;
    private bool disposed;

    /// <summary>Jobs of a Screening run with their CV and failed counts, from the run's own items.</summary>
    public IReadOnlyList<RunJobSummary> Jobs => Run is null ? [] : Run.Items
        .GroupBy(i => i.JobId)
        .Select(g => new RunJobSummary(g.Key, g.First().JobTitle, g.Count(), g.Count(i => i.Status == EvaluationStatuses.Failed)))
        .OrderBy(j => j.JobId).ToList();

    /// <summary>Loads run <paramref name="id"/>. Same id = refresh: current data stays visible until the new data arrives.</summary>
    public async Task LoadAsync(int id)
    {
        if (disposed) return;
        request?.Cancel(); request?.Dispose();
        request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = request.Token;
        var current = ++generation;
        if (id != RunId)
        {
            RunId = id; Run = null; Failures = null; FailuresError = null; FailuresLoading = false;
            ++failuresGeneration; failuresRequest?.Cancel();
        }
        Loading = true; Error = null; NotFound = false;
        try
        {
            var result = await api.GetRunAsync(id, ct);
            if (disposed || current != generation) return;
            if (!result.IsSuccess)
            {
                NotFound = result.StatusCode == 404;
                // 404: the run is gone. Other errors on a refresh keep the same run's last data with a warning.
                if (NotFound) { Run = null; Failures = null; FailuresError = null; }
                Error = NotFound ? $"Không có lượt chấm #{id}." : RunHistoryWorkspace.Message(result.StatusCode, result.ErrorMessage, "lượt chấm");
                return;
            }
            if (result.Data!.RunId != id || result.Data.Items is null || result.Data.Items.Any(i => i is null))
            {
                Error = $"Dữ liệu trả về không khớp lượt chấm #{id}. Hãy thử lại.";
                return;
            }
            Run = result.Data;
            Loading = false;
            Changed?.Invoke();
            await LoadFailuresAsync(id, current);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (!disposed && current == generation) Loading = false; }
    }

    /// <summary>Retry only the failures list; the summary on screen is untouched.</summary>
    public Task RetryFailuresAsync() => Run is null || disposed ? Task.CompletedTask : LoadFailuresAsync(RunId, generation);

    private async Task LoadFailuresAsync(int id, int runGeneration)
    {
        failuresRequest?.Cancel(); failuresRequest?.Dispose();
        failuresRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = failuresRequest.Token;
        var current = ++failuresGeneration;
        FailuresLoading = true; FailuresError = null;
        try
        {
            var result = await api.GetFailuresAsync(id, ct);
            if (disposed || current != failuresGeneration || runGeneration != generation || RunId != id) return;
            if (!result.IsSuccess)
            {
                FailuresError = result.StatusCode == 404
                    ? "API chưa trả danh sách hồ sơ lỗi cho lượt này. Kiểm tra API đã cập nhật (A25)."
                    : RunHistoryWorkspace.Message(result.StatusCode, result.ErrorMessage, "danh sách hồ sơ lỗi");
                return;
            }
            if (result.Data!.RunId != id || result.Data.Items is null || result.Data.Items.Any(i => i is null))
            { FailuresError = "Danh sách hồ sơ lỗi không khớp lượt đang xem. Hãy thử lại."; return; }
            Failures = result.Data;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (!disposed && current == failuresGeneration) FailuresLoading = false; }
    }

    public void Dispose()
    {
        disposed = true; ++generation; ++failuresGeneration;
        lifetime.Cancel(); request?.Cancel(); failuresRequest?.Cancel();
        request?.Dispose(); failuresRequest?.Dispose(); lifetime.Dispose();
    }
}

public sealed record RunJobSummary(int JobId, string Title, int Count, int FailedCount);
