namespace SmartCv.Core.Ai;

/// <summary>Kết quả xin một lượt chấm thử.</summary>
public enum PreviewQuotaStatus
{
    /// <summary>Được chấm: đã giữ một lượt và một chỗ chạy.</summary>
    Granted,
    /// <summary>Đã dùng hết số lượt trong cửa sổ thời gian.</summary>
    Exhausted,
    /// <summary>Còn lượt nhưng đang có đủ số lượt chấm thử chạy cùng lúc.</summary>
    Busy
}

/// <summary>Trạng thái bộ đếm tại một thời điểm. ResetsAt: lúc lượt cũ nhất trong cửa sổ được trả lại (null khi chưa dùng lượt nào).</summary>
public sealed record PreviewQuotaSnapshot(
    int Remaining,
    int Limit,
    TimeSpan Window,
    DateTimeOffset? ResetsAt,
    int InFlight,
    int MaxConcurrent);

/// <summary>
/// A41: giới hạn số lượt chấm thử dùng chung cả ứng dụng (chưa có tài khoản trước M6), cửa sổ trượt.
/// Mỗi lượt giữ một vé (ticket) có số riêng, nên khi cần trả lại thì trả ĐÚNG vé của request đó,
/// không bao giờ trả nhầm vé của request khác chạy song song. Kiểm tra và giữ vé trong cùng một khóa (nguyên tử).
/// Đồng thời giới hạn số lượt chạy cùng lúc (<see cref="MaxConcurrent"/>) để request không xếp hàng chờ Gemini quá lâu.
/// Singleton, an toàn đa luồng, chỉ đúng trong MỘT tiến trình (nhiều instance cần bộ đếm dùng chung như Redis/database).
/// Đồng hồ là <see cref="TimeProvider"/> để test điều khiển được thời gian.
/// </summary>
public sealed class PreviewQuota
{
    private readonly Lock _sync = new();
    private readonly List<Use> _uses = [];
    private readonly TimeProvider _time;
    private long _lastTicket;
    private int _inFlight;

    public PreviewQuota(int limitPerWindow, TimeSpan window, int maxConcurrent, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limitPerWindow, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        Limit = limitPerWindow;
        Window = window;
        MaxConcurrent = maxConcurrent;
        _time = timeProvider ?? TimeProvider.System;
    }

    public int Limit { get; }
    public TimeSpan Window { get; }
    public int MaxConcurrent { get; }

    /// <summary>Xem trạng thái, không tiêu lượt.</summary>
    public PreviewQuotaSnapshot Peek()
    {
        lock (_sync)
        {
            Prune(_time.GetUtcNow());
            return Snapshot();
        }
    }

    /// <summary>
    /// Xin một lượt. Hết lượt được ưu tiên báo trước "đang bận" vì có thời điểm thử lại rõ ràng.
    /// Lượt được cấp phải được <see cref="PreviewQuotaReservation.Dispose"/> (dùng using) để trả chỗ chạy.
    /// </summary>
    public PreviewQuotaReservation TryReserve()
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            if (_uses.Count >= Limit) return new PreviewQuotaReservation(this, 0, PreviewQuotaStatus.Exhausted, Snapshot(), now);
            if (_inFlight >= MaxConcurrent) return new PreviewQuotaReservation(this, 0, PreviewQuotaStatus.Busy, Snapshot(), now);

            var ticket = ++_lastTicket;
            _uses.Add(new Use(ticket, now));
            _inFlight++;
            return new PreviewQuotaReservation(this, ticket, PreviewQuotaStatus.Granted, Snapshot(), now);
        }
    }

    /// <summary>Bỏ đúng vé <paramref name="ticket"/> khỏi cửa sổ. false: vé không còn (đã trả hoặc đã hết hạn).</summary>
    internal bool Refund(long ticket)
    {
        lock (_sync)
        {
            var index = _uses.FindIndex(u => u.Ticket == ticket);
            if (index < 0) return false;
            _uses.RemoveAt(index);
            return true;
        }
    }

    internal void Exit()
    {
        lock (_sync)
        {
            if (_inFlight > 0) _inFlight--;
        }
    }

    // Quét toàn bộ (tối đa vài trăm phần tử) thay vì chỉ đầu danh sách: vẫn đúng nếu đồng hồ hệ thống bị chỉnh lùi.
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - Window;
        _uses.RemoveAll(u => u.At <= cutoff);
    }

    private PreviewQuotaSnapshot Snapshot()
    {
        DateTimeOffset? resetsAt = null;
        foreach (var use in _uses)
            if (resetsAt is null || use.At + Window < resetsAt) resetsAt = use.At + Window;
        return new PreviewQuotaSnapshot(Math.Max(0, Limit - _uses.Count), Limit, Window, resetsAt, _inFlight, MaxConcurrent);
    }

    private readonly record struct Use(long Ticket, DateTimeOffset At);
}

/// <summary>
/// Một lần xin lượt chấm thử. Granted: giữ một vé trong cửa sổ và một chỗ chạy cho tới khi Dispose.
/// Gọi <see cref="Refund"/> khi CHẮC CHẮN chưa gửi gì tới AI (ví dụ thiếu API key): chỉ vé của chính lần này được trả lại.
/// </summary>
public sealed class PreviewQuotaReservation : IDisposable
{
    private readonly PreviewQuota _quota;
    private int _refunded;
    private int _released;

    internal PreviewQuotaReservation(PreviewQuota quota, long ticket, PreviewQuotaStatus status, PreviewQuotaSnapshot snapshot, DateTimeOffset decidedAt)
    {
        _quota = quota;
        Ticket = ticket;
        Status = status;
        Snapshot = snapshot;
        DecidedAt = decidedAt;
    }

    public PreviewQuotaStatus Status { get; }
    public bool IsGranted => Status == PreviewQuotaStatus.Granted;

    /// <summary>Số vé (tăng dần, duy nhất trong tiến trình); 0 khi không được cấp.</summary>
    public long Ticket { get; }

    /// <summary>Trạng thái bộ đếm ngay sau khi quyết định.</summary>
    public PreviewQuotaSnapshot Snapshot { get; }

    public DateTimeOffset DecidedAt { get; }

    /// <summary>Hết lượt: còn bao lâu nữa có lượt mới (ít nhất 1 giây). Các trường hợp khác: null.</summary>
    public TimeSpan? RetryAfter => Status == PreviewQuotaStatus.Exhausted && Snapshot.ResetsAt is { } resetsAt
        ? TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((resetsAt - DecidedAt).TotalSeconds)))
        : null;

    public bool Refunded => Volatile.Read(ref _refunded) == 1;

    /// <summary>Trả lại vé của chính lần này. Gọi nhiều lần cũng chỉ trả một lần; lần không được cấp thì không làm gì.</summary>
    public bool Refund() =>
        IsGranted && Interlocked.Exchange(ref _refunded, 1) == 0 && _quota.Refund(Ticket);

    /// <summary>Trả chỗ chạy (không trả vé). Gọi nhiều lần cũng chỉ trả một lần.</summary>
    public void Dispose()
    {
        if (IsGranted && Interlocked.Exchange(ref _released, 1) == 0) _quota.Exit();
    }
}
