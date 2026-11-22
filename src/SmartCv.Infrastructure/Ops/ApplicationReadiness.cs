using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ops;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Ops;

/// <summary>Read-only DB/schema checks and disposable storage probe; no AI calls, native OCR load or data repair.</summary>
public sealed class ApplicationReadiness(SmartCvDbContext db, ICvFileStorage storage, IOptions<OcrOptions> ocr, IOptions<EmailOptions> email,
    TimeProvider time, ILogger<ApplicationReadiness> logger)
{
    public async Task<HealthReportDto> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var checks = await Task.WhenAll(DatabaseAsync(ct), UploadsAsync(ct), OcrAsync(ct), EmailAsync(ct));
        return HealthRules.Combine(checks, time.GetUtcNow().UtcDateTime);
    }
    private async Task<HealthCheckResult> DatabaseAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3)); // fits inside the Web health client's existing five-second timeout
        try
        {
            if (!await db.Database.CanConnectAsync(timeout.Token)) return new("database", false, "Không kết nối được database.");
            if (db.Database.IsSqlServer())
            {
                var schema = await DatabaseSchemaDiagnostics.InspectAsync(db, timeout.Token);
                if (!schema.IsReady)
                {
                    logger.LogWarning("Database schema not ready: {Count} pending migrations, {MissingCount} missing objects.", schema.PendingMigrations.Count, schema.MissingObjects.Count);
                    return new("database", false, "Schema chưa sẵn sàng. Cần kiểm tra migration đã được phê duyệt.");
                }
            }
            return new("database", true, "Kết nối và schema sẵn sàng.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ct.ThrowIfCancellationRequested();
            Failure("database", error);
            return new("database", false, "Không kiểm tra được database/schema trong thời gian cho phép.");
        }
    }
    private async Task<HealthCheckResult> UploadsAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (storage is not ICvStorageProbe probe) return new("uploads", false, "Kho CV chưa hỗ trợ kiểm tra sẵn sàng.");
            await probe.CheckWritableAsync(ct);
            return new("uploads", true, "Kho CV đọc/ghi được dữ liệu kiểm tra.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Failure("uploads", error); return new("uploads", false, "Không ghi được kho CV. Kiểm tra quyền của tiến trình API."); }
    }
    private Task<HealthCheckResult> OcrAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            foreach (var name in HealthRules.OcrFiles(ocr.Value.Languages))
            {
                ct.ThrowIfCancellationRequested();
                using var file = new FileStream(Path.Combine(ocr.Value.TessdataPath, name), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.ReadByte() < 0) return Task.FromResult(new HealthCheckResult("ocr", false, "Dữ liệu ngôn ngữ OCR đang thiếu hoặc rỗng."));
            }
            // Presence/readability only. Actual OCR inference is verified separately by the existing extraction tests.
            return Task.FromResult(new HealthCheckResult("ocr", true, "Đọc được dữ liệu ngôn ngữ OCR đã cấu hình."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Failure("ocr", error); return Task.FromResult(new HealthCheckResult("ocr", false, "Không đọc được dữ liệu ngôn ngữ OCR đã cấu hình.")); }
    }
    private Task<HealthCheckResult> EmailAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var value = email.Value;
        return Task.FromResult(new HealthCheckResult("email", true,
            value.Mode == "Disabled" ? "Email bị tắt theo cấu hình." :
            "Cấu hình email đã được nạp; không thử kết nối SMTP."));
    }
    private void Failure(string code, Exception error) => logger.LogWarning("Health check {Code} failed ({FailureType}).", code, error.GetType().Name);
}
