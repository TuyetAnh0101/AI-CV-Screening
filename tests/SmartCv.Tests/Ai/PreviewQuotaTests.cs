using SmartCv.Core.Ai;

namespace SmartCv.Tests;

/// <summary>A41: bộ đếm lượt chấm thử dùng chung cả ứng dụng (cửa sổ trượt, vé riêng, giới hạn chạy cùng lúc).</summary>
public class PreviewQuotaTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 7, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hour = TimeSpan.FromMinutes(60);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static (PreviewQuota Quota, ManualClock Clock) Create(int limit, int maxConcurrent = 10)
    {
        var clock = new ManualClock(Start);
        return (new PreviewQuota(limit, Hour, maxConcurrent, clock), clock);
    }

    private static void UseOnce(PreviewQuota quota)
    {
        using var reservation = quota.TryReserve();
        Assert.Equal(PreviewQuotaStatus.Granted, reservation.Status);
    }

    [Fact]
    public void Grants_until_the_limit_then_reports_exhausted_with_reset_time()
    {
        var (quota, _) = Create(limit: 3);
        for (var i = 0; i < 3; i++) UseOnce(quota);

        using var rejected = quota.TryReserve();

        Assert.Equal(PreviewQuotaStatus.Exhausted, rejected.Status);
        Assert.False(rejected.IsGranted);
        Assert.Equal(0, rejected.Ticket);
        Assert.Equal(0, rejected.Snapshot.Remaining);
        Assert.Equal(Start + Hour, rejected.Snapshot.ResetsAt);
        Assert.Equal(Hour, rejected.RetryAfter);
        Assert.Equal(0, quota.Peek().Remaining);   // lần bị từ chối không tính thêm lượt
    }

    [Fact]
    public void Peek_never_consumes_a_try()
    {
        var (quota, _) = Create(limit: 2);
        for (var i = 0; i < 5; i++) Assert.Equal(2, quota.Peek().Remaining);

        var empty = quota.Peek();
        Assert.Null(empty.ResetsAt);
        Assert.Equal((2, Hour, 0, 10), (empty.Limit, empty.Window, empty.InFlight, empty.MaxConcurrent));
    }

    [Fact]
    public void Sliding_window_returns_each_try_one_window_after_its_own_use()
    {
        var (quota, clock) = Create(limit: 2);
        UseOnce(quota);                              // 07:00
        clock.Advance(TimeSpan.FromMinutes(10));
        UseOnce(quota);                              // 07:10
        Assert.Equal(PreviewQuotaStatus.Exhausted, quota.TryReserve().Status);

        clock.Advance(TimeSpan.FromMinutes(49));     // 07:59: chưa đủ 60 phút
        Assert.Equal(PreviewQuotaStatus.Exhausted, quota.TryReserve().Status);

        clock.Advance(TimeSpan.FromMinutes(1));      // 08:00: lượt 07:00 được trả lại, lượt 07:10 vẫn còn
        var snapshot = quota.Peek();
        Assert.Equal(1, snapshot.Remaining);
        Assert.Equal(Start + TimeSpan.FromMinutes(70), snapshot.ResetsAt);
        UseOnce(quota);
        Assert.Equal(0, quota.Peek().Remaining);
    }

    [Fact]
    public void Retry_after_counts_from_the_decision_time_and_is_at_least_one_second()
    {
        var (quota, clock) = Create(limit: 1);
        UseOnce(quota);
        clock.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59.5));

        using var rejected = quota.TryReserve();

        Assert.Equal(TimeSpan.FromSeconds(1), rejected.RetryAfter);
    }

    [Fact]
    public void Refund_returns_exactly_this_ticket_not_the_newest_one()
    {
        var (quota, clock) = Create(limit: 3);
        var first = quota.TryReserve();              // 07:00
        clock.Advance(TimeSpan.FromMinutes(1));
        using var second = quota.TryReserve();       // 07:01
        clock.Advance(TimeSpan.FromMinutes(1));
        using var third = quota.TryReserve();        // 07:02

        Assert.True(first.Refund());
        first.Dispose();

        var snapshot = quota.Peek();
        Assert.Equal(1, snapshot.Remaining);
        // Mẫu ZIP bỏ vé MỚI nhất (07:02) nên lượt sớm nhất sẽ là 07:00; đúng phải là vé 07:01 còn lại.
        Assert.Equal(Start + TimeSpan.FromMinutes(1) + Hour, snapshot.ResetsAt);
        Assert.True(first.Refunded);
        Assert.False(second.Refunded);
        Assert.Equal([1L, 2L, 3L], new[] { first.Ticket, second.Ticket, third.Ticket });
    }

    [Fact]
    public void Refund_is_idempotent_and_rejected_reservations_cannot_refund()
    {
        var (quota, _) = Create(limit: 1);
        using var granted = quota.TryReserve();
        using var rejected = quota.TryReserve();

        Assert.False(rejected.Refund());
        Assert.False(rejected.Refunded);
        Assert.Equal(0, quota.Peek().Remaining);

        Assert.True(granted.Refund());
        Assert.False(granted.Refund());
        Assert.Equal(1, quota.Peek().Remaining);
    }

    [Fact]
    public void Refund_after_the_try_already_expired_changes_nothing()
    {
        var (quota, clock) = Create(limit: 2);
        using var old = quota.TryReserve();
        clock.Advance(Hour + TimeSpan.FromSeconds(1));
        using var fresh = quota.TryReserve();

        Assert.False(old.Refund());
        Assert.Equal(1, quota.Peek().Remaining);   // vé mới không bị trả nhầm
    }

    [Fact]
    public void Busy_when_max_concurrent_is_running_without_consuming_and_dispose_frees_the_slot()
    {
        var (quota, _) = Create(limit: 10, maxConcurrent: 1);
        var running = quota.TryReserve();
        Assert.Equal(1, quota.Peek().InFlight);

        using (var busy = quota.TryReserve())
        {
            Assert.Equal(PreviewQuotaStatus.Busy, busy.Status);
            Assert.Null(busy.RetryAfter);
            Assert.Equal(9, busy.Snapshot.Remaining);
        }

        running.Dispose();
        running.Dispose();                          // gọi lại không trả chỗ lần hai
        Assert.Equal(0, quota.Peek().InFlight);
        Assert.Equal(9, quota.Peek().Remaining);    // trả chỗ chạy KHÔNG trả lượt

        using var next = quota.TryReserve();
        Assert.Equal(PreviewQuotaStatus.Granted, next.Status);
    }

    [Fact]
    public void Exhausted_is_reported_before_busy()
    {
        var (quota, _) = Create(limit: 1, maxConcurrent: 1);
        using var running = quota.TryReserve();

        using var rejected = quota.TryReserve();

        Assert.Equal(PreviewQuotaStatus.Exhausted, rejected.Status);
    }

    [Fact]
    public async Task Hundred_concurrent_requests_get_exactly_the_limit()
    {
        var quota = new PreviewQuota(30, Hour, maxConcurrent: 100);
        using var gate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            return quota.TryReserve();
        })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        var granted = results.Where(r => r.IsGranted).ToList();
        Assert.Equal(30, granted.Count);
        Assert.Equal(70, results.Count(r => r.Status == PreviewQuotaStatus.Exhausted));
        Assert.Equal(30, granted.Select(r => r.Ticket).Distinct().Count());
        Assert.Equal(0, quota.Peek().Remaining);
        Assert.Equal(30, quota.Peek().InFlight);
        foreach (var r in results) r.Dispose();
        Assert.Equal(0, quota.Peek().InFlight);
    }

    [Fact]
    public async Task Concurrent_refunds_restore_exactly_their_own_tickets()
    {
        var (quota, clock) = Create(limit: 50, maxConcurrent: 50);
        var reservations = new List<PreviewQuotaReservation>();
        for (var i = 0; i < 50; i++)
        {
            reservations.Add(quota.TryReserve());
            clock.Advance(TimeSpan.FromSeconds(1));   // vé thứ i dùng lúc 07:00:i
        }

        // Trả song song các vé CHẴN (0, 2, 4…) trong khi các vé lẻ vẫn đang chạy
        await Task.WhenAll(reservations.Where((_, i) => i % 2 == 0).Select(r => Task.Run(r.Refund)));

        var snapshot = quota.Peek();
        Assert.Equal(25, snapshot.Remaining);
        Assert.Equal(Start + TimeSpan.FromSeconds(1) + Hour, snapshot.ResetsAt);   // vé lẻ sớm nhất (07:00:01) còn lại
        Assert.All(reservations.Where((_, i) => i % 2 == 1), r => Assert.False(r.Refunded));
        foreach (var r in reservations) r.Dispose();
    }

    [Theory]
    [InlineData(0, 60, 1)]
    [InlineData(-1, 60, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 60, 0)]
    public void Invalid_arguments_are_rejected_not_clamped(int limit, int minutes, int maxConcurrent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewQuota(limit, TimeSpan.FromMinutes(minutes), maxConcurrent));
    }
}
