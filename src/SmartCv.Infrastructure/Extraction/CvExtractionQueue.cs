using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SmartCv.Core.Extraction;

namespace SmartCv.Infrastructure.Extraction;

/// <summary>
/// Hàng đợi trong bộ nhớ (Channel). Đăng ký Singleton để nơi xếp (upload, API đọc lại) và nơi lấy (worker) dùng chung.
/// Một CvId đang nằm trong hàng thì không được xếp thêm lần nữa (tránh bấm "Đọc lại" nhiều lần làm OCR lặp).
/// Tắt API thì hàng đợi mất, nhưng không sao: khi khởi động lại, worker tự nạp lại các CV còn Pending.
/// </summary>
public sealed class CvExtractionQueue : ICvExtractionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    // CvId đang chờ trong hàng (chưa được worker lấy ra)
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();

    public async ValueTask EnqueueAsync(Guid cvId, CancellationToken cancellationToken = default)
    {
        if (!_queued.TryAdd(cvId, 0)) return;   // đã có trong hàng

        try
        {
            await _channel.Writer.WriteAsync(cvId, cancellationToken);
        }
        catch
        {
            _queued.TryRemove(cvId, out _);
            throw;
        }
    }

    public async IAsyncEnumerable<Guid> DequeueAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var cvId in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            // Lấy ra khỏi danh sách trước khi xử lý: trong lúc đang đọc, CV có thể được xếp lại (API đọc lại)
            _queued.TryRemove(cvId, out _);
            yield return cvId;
        }
    }

    /// <summary>Số CV đang chờ (dùng cho test và log).</summary>
    public int Count => _queued.Count;
}
