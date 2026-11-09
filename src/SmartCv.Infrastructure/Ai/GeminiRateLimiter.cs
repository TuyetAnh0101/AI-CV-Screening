using System.Diagnostics;
using Microsoft.Extensions.Options;
using SmartCv.Infrastructure.Gemini;

namespace SmartCv.Infrastructure.Ai;

/// <summary>
/// Giữ khoảng cách tối thiểu giữa 2 lần gọi Gemini (mặc định 4,5 giây), để không vượt hạn mức gọi mỗi phút.
/// Mỗi lần gọi đặt trước một mốc thời gian rồi chờ NGOÀI khóa, nên không giữ khóa trong lúc chờ hay lúc gọi mạng.
/// Singleton: chỉ điều phối trong MỘT instance ứng dụng; chạy nhiều instance cần giới hạn dùng chung (ví dụ Redis).
/// </summary>
public sealed class GeminiRateLimiter(IOptions<GeminiOptions> options)
{
    private readonly Lock _sync = new();
    private readonly TimeSpan _minDelay = TimeSpan.FromMilliseconds(Math.Max(0, options.Value.MinDelayBetweenCallsMs));
    private readonly Stopwatch _clock = Stopwatch.StartNew();   // đồng hồ đơn điệu, không bị ảnh hưởng khi đổi giờ máy
    private TimeSpan _nextSlot = TimeSpan.Zero;

    public async Task WaitTurnAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        TimeSpan wait;
        lock (_sync)
        {
            var now = _clock.Elapsed;
            var slot = _nextSlot > now ? _nextSlot : now;
            _nextSlot = slot + _minDelay;
            wait = slot - now;
        }

        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }
}
