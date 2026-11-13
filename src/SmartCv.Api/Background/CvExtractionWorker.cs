using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Extraction;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Extraction;

namespace SmartCv.Api.Background;

/// <summary>
/// Tiến trình chạy nền suốt thời gian API chạy: lấy từng CvId trong hàng đợi và đọc chữ (A7).
/// Khi API khởi động: tự xếp lại các CV còn Pending (ví dụ lần trước tắt API khi đang đọc dở).
/// </summary>
public sealed class CvExtractionWorker(
    ICvExtractionQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<CvExtractionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RequeuePendingAsync(stoppingToken);
            await foreach (var cvId in queue.DequeueAllAsync(stoppingToken))
            {
                try
                {
                    // Mỗi CV một scope mới: DbContext mới, không giữ dữ liệu cũ giữa các lần
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<CvExtractionProcessor>();
                    await processor.ProcessAsync(cvId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;   // API đang tắt; CV còn Pending sẽ được xếp lại ở lần chạy sau
                }
                catch (Exception ex)
                {
                    // Một CV lỗi không được làm dừng cả tiến trình
                    logger.LogError(ex, "Lỗi khi đọc chữ CV {CvId}", cvId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // API đang tắt
        }
    }

    private async Task RequeuePendingAsync(CancellationToken ct)
    {
        var failures = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
                var pending = await db.Cvs.AsNoTracking()
                    .Where(c => c.AnonymizedAt == null && c.ExtractionStatus == ExtractionStatuses.Pending)
                    .OrderBy(c => c.UploadedAt)
                    .Select(c => c.CvId)
                    .ToListAsync(ct);

                foreach (var id in pending) await queue.EnqueueAsync(id, ct);

                if (pending.Count > 0)
                    logger.LogInformation("Xếp lại {Count} CV đang chờ đọc chữ", pending.Count);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException("Pending CV recovery cancelled with the worker.", ex, ct);
                failures++;
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures - 1, 5))));
                logger.LogError(ex, "Không nạp được danh sách CV đang chờ đọc chữ; thử lại sau {Seconds} giây (lần {Attempt})",
                    delay.TotalSeconds, failures);
                // Only retry the pending-ID read. The queue deduplicates IDs; no OCR or AI call is replayed here.
                await Task.Delay(delay, ct);
            }
        }
    }
}
