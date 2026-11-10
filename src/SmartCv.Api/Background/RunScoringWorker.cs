using Microsoft.Extensions.Options;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Api.Background;

/// <summary>
/// A11: tiến trình chấm điểm nền (chỉ chạy khi Scoring:WorkerEnabled = true).
/// Mỗi vòng: giữ khóa worker → lấy các lượt Queued/Running (cũ trước) → chấm các hồ sơ còn Pending → lặp lại.
/// Không có việc thì nghỉ PollIntervalSeconds giây; database lỗi thì nghỉ tăng dần, tối đa 60 giây.
/// Trạng thái nằm trong DATABASE, không giữ hàng đợi trong bộ nhớ, nên tắt / bật lại API vẫn chấm tiếp đúng chỗ.
/// </summary>
public sealed class RunScoringWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ScoringOptions> options,
    ILogger<RunScoringWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaxErrorDelay = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled)
        {
            // Warning (không phải Information): khi tắt, mọi lượt tạo qua POST /api/runs đứng mãi ở Queued, 0%.
            logger.LogWarning("Tiến trình chấm điểm đang TẮT (Scoring:WorkerEnabled = false): lượt phân tích mới sẽ đứng ở trạng thái Queued cho tới khi bật worker");
            return;
        }

        var idle = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        logger.LogInformation("Tiến trình chấm điểm đã chạy (kiểm tra mỗi {Seconds} giây)", idle.TotalSeconds);

        var consecutiveErrors = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork;
            try
            {
                didWork = await RunPassAsync(stoppingToken);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Mất kết nối database...: không làm sập API, trạng thái đã lưu vẫn nguyên
                consecutiveErrors++;
                didWork = false;
                logger.LogError(ex, "Lỗi trong tiến trình chấm điểm (lần {Count} liên tiếp)", consecutiveErrors);
            }

            if (didWork) continue;   // vừa chấm được: kiểm tra việc tiếp theo ngay

            try { await Task.Delay(NextDelay(idle, consecutiveErrors), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("Tiến trình chấm điểm đã dừng");
    }

    private async Task<bool> RunPassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<RunScoringProcessor>();

        await using var workerLock = await coordinator.TryAcquireWorkerLockAsync(ct);
        if (workerLock is null) return false;   // instance khác đang chấm

        var didWork = false;
        Exception? firstError = null;
        foreach (var runId in await coordinator.FindActiveRunsAsync(ct))
        {
            try
            {
                // Mỗi lượt một scope: lỗi của lượt này không ảnh hưởng DbContext của lượt khác.
                // Xét MỌI lượt: lượt đang chờ CV đọc chữ không chặn lượt khác.
                using var runScope = scopeFactory.CreateScope();
                var processor = runScope.ServiceProvider.GetRequiredService<RunScoringProcessor>();
                if (await processor.ProcessRunAsync(runId, ct) > 0) didWork = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Lỗi khi xử lý lượt chấm {RunId}", runId);
                firstError ??= ex;
            }
        }

        // Báo lỗi cho vòng ngoài để nghỉ tăng dần; lượt nào chấm được vẫn đã chấm
        return firstError is null ? didWork : throw new InvalidOperationException("Có lượt chấm xử lý lỗi", firstError);
    }

    /// <summary>Không lỗi: nghỉ theo chu kỳ. Lỗi liên tiếp: gấp đôi mỗi lần, không quá 60 giây.</summary>
    public static TimeSpan NextDelay(TimeSpan idle, int consecutiveErrors)
    {
        if (consecutiveErrors <= 0) return idle;
        var seconds = idle.TotalSeconds * Math.Pow(2, Math.Min(consecutiveErrors, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxErrorDelay.TotalSeconds));
    }
}
