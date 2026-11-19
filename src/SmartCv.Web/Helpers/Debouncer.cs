namespace SmartCv.Web.Helpers;

/// <summary>Only the last input runs; component events await this task so exceptions are observed.</summary>
public sealed class Debouncer(TimeSpan? delay = null) : IDisposable
{
    public TimeSpan Delay { get; } = delay ?? TimeSpan.FromMilliseconds(400);
    private CancellationTokenSource? pending;
    private bool disposed;
    public async Task Trigger(Func<Task> action)
    {
        if (disposed) return;
        Cancel();
        using var current = new CancellationTokenSource();
        pending = current;
        try
        {
            await Task.Delay(Delay, current.Token);
            if (!disposed && !current.IsCancellationRequested) await action();
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        finally { if (ReferenceEquals(pending, current)) pending = null; }
    }
    public void Cancel() => pending?.Cancel();
    public void Dispose() { disposed = true; Cancel(); }
}
