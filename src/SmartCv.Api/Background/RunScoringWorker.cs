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
            logger.LogInformation("Tiến trình chấm điểm đang TẮT (Scoring:WorkerEnabled = false)");
            return;
        }

        var idle = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        logger.LogInformation("Tiến trình chấm điểm đã chạy (kiểm tra mỗi {Seconds} giây)", idle.TotalSeconds);
        var (database, userAccess) = await CheckDatabaseAsync(stoppingToken);

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
                // Không mở được database: mã lỗi rõ ràng (4060, 924...) HOẶC lúc khởi động đã thấy database không MULTI_USER.
                // Với SINGLE_USER, kết nối thứ hai có lúc bị từ chối (4060), có lúc bị treo tới hết thời gian chờ (-2),
                // nên phải dựa cả vào kết quả kiểm tra lúc khởi động. Chưa chấm hồ sơ nào, chưa gọi Gemini, trạng thái lượt giữ nguyên.
                if (WorkerDatabaseDiagnostics.TryGetDatabaseAccessError(ex, out _) || !WorkerDatabaseDiagnostics.SupportsWorker(userAccess))
                    logger.LogError(ex,
                        "Tiến trình chấm điểm không mở được database {Database} (chế độ lúc khởi động: {UserAccess}, lỗi SQL {SqlNumber}, lần {Count} liên tiếp). " +
                        "Mỗi vòng chấm cần 2 kết nối đồng thời (khóa worker + xử lý lượt), nên các lượt sẽ đứng ở Queued. " +
                        "Kiểm tra database có ở chế độ MULTI_USER và tài khoản có quyền truy cập. {Hint}",
                        database, userAccess ?? "(chưa đọc được)", WorkerDatabaseDiagnostics.FirstSqlErrorNumber(ex)?.ToString() ?? "-",
                        consecutiveErrors, WorkerDatabaseDiagnostics.FixHint(database));
                else
                    logger.LogError(ex, "Lỗi trong tiến trình chấm điểm (lần {Count} liên tiếp)", consecutiveErrors);
            }

            if (didWork) continue;   // vừa chấm được: kiểm tra việc tiếp theo ngay

            try { await Task.Delay(NextDelay(idle, consecutiveErrors), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("Tiến trình chấm điểm đã dừng");
    }

    /// <summary>
    /// Kiểm tra một lần lúc worker khởi động: database phải cho phép nhiều kết nối đồng thời.
    /// Chỉ ĐỌC và chỉ ghi log; không dừng worker (sửa database xong là vòng sau tự chạy, không cần khởi động lại API).
    /// Trả tên database và chế độ truy cập (null: không phải SQL Server hoặc chưa đọc được) để các log lỗi sau dùng lại.
    /// </summary>
    private async Task<(string Database, string? UserAccess)> CheckDatabaseAsync(CancellationToken ct)
    {
        var database = "(không rõ)";
        string? access = null;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartCv.Infrastructure.Data.SmartCvDbContext>();
            database = WorkerDatabaseDiagnostics.DatabaseName(db);
            access = await WorkerDatabaseDiagnostics.ReadUserAccessAsync(db, ct);
            if (!WorkerDatabaseDiagnostics.SupportsWorker(access))
                logger.LogError(
                    "Tiến trình chấm điểm KHÔNG chạy được: database {Database} đang ở chế độ {UserAccess}. " +
                    "Mỗi vòng chấm cần 2 kết nối đồng thời (khóa worker sp_getapplock + DbContext xử lý lượt), " +
                    "nên mọi lượt sẽ đứng ở Queued và Gemini không được gọi. {Hint}",
                    database, access, WorkerDatabaseDiagnostics.FixHint(database));
            else if (access is not null)
                logger.LogInformation("Database {Database} ở chế độ {UserAccess}: tiến trình chấm điểm dùng được", database, access);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Chưa kết nối được lúc khởi động: vòng chấm sẽ tự thử lại và ghi log lỗi chi tiết
            logger.LogWarning("Chưa kiểm tra được chế độ truy cập của database {Database} ({ErrorType}); tiến trình chấm điểm sẽ thử lại",
                database, ex.GetType().Name);
        }
        return (database, access);
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
