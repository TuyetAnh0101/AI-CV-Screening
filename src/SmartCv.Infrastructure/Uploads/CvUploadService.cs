using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Uploads;

/// <summary>
/// Xử lý file CV tải lên.
/// A4: nhận file. A5: kiểm tra rỗng, dung lượng, đuôi file, nội dung thật, trùng hash (trong lần tải và trong database).
/// A6: lưu file hợp lệ vào Uploads/CV/yyyy/MM/&lt;GUID&gt;.ext và thêm dòng vào bảng Cvs (ExtractionStatus = Pending),
///     trả Accepted + CvId. File bị từ chối trả Rejected kèm ErrorCode, ErrorMessage (thêm mã SaveFailed).
/// A7: sau khi CV đã lưu chắc chắn, xếp CvId vào hàng đợi để tiến trình nền đọc chữ (không chờ đọc xong).
/// </summary>
public sealed class CvUploadService(
    SmartCvDbContext db,
    ICvFileStorage storage,
    ICvExtractionQueue extractionQueue,
    IOptions<CvUploadOptions> options,
    ILogger<CvUploadService> logger) : ICvUploadService
{
    private readonly CvUploadOptions _options = options.Value;

    public async Task<CvUploadResponse> UploadAsync(IReadOnlyList<CvUploadFile> files, CvUploadSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!CvSourceChannels.IsValid(source.SourceChannel) || source.ConsentConfirmedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Nguồn CV và thời điểm xác nhận UTC không hợp lệ.", nameof(source));
        // Giữ đúng thứ tự file người dùng gửi: kết quả thứ i ứng với file thứ i
        var results = new CvUploadItemResult[files.Count];
        var passed = new List<PassedFile>();   // file qua được bước 1–2, chờ kiểm tra trùng

        // ---- Bước 1–2: kiểm tra từng file (không cần database) ----
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];

            // Chỉ giữ tên file, bỏ phần đường dẫn mà một số trình duyệt cũ gửi kèm (vd: C:\Users\...\cv.pdf).
            // Đổi '\' thành '/' trước để bỏ được đường dẫn kiểu Windows cả khi API chạy trên Linux.
            var name = Path.GetFileName(file.FileName.Replace('\\', '/'));

            // Bước 1: rỗng, dung lượng, đuôi file – chưa cần đọc nội dung
            var basic = CvFileValidator.CheckBasic(name, file.Length, _options);
            if (!basic.IsValid)
            {
                results[i] = Rejected(file, name, basic.ErrorCode!, basic.ErrorMessage!);
                continue;
            }

            // Bước 2: nội dung thật – đọc tối đa 1024 byte đầu (magic bytes)
            CvFileCheck signature;
            await using (var stream = file.OpenReadStream())
            {
                var header = new byte[CvFileValidator.HeaderLength];
                var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
                signature = CvFileValidator.CheckSignature(basic.Extension!, header.AsSpan(0, read));
            }

            if (!signature.IsValid)
            {
                results[i] = Rejected(file, name, signature.ErrorCode!, signature.ErrorMessage!);
                continue;
            }

            // Mở lại luồng từ đầu để tính SHA-256 toàn bộ nội dung (đọc dần, không nạp cả file vào bộ nhớ)
            string hash;
            await using (var stream = file.OpenReadStream())
            {
                hash = await FileHasher.ComputeSha256Async(stream, cancellationToken);
            }

            passed.Add(new PassedFile(i, file, name, signature.Extension!, signature.ContentType!, hash));
        }

        // ---- Bước 3: trùng nhau trong cùng một lần tải (giữ file đầu tiên) ----
        var firstByHash = new Dictionary<string, PassedFile>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<PassedFile>();
        foreach (var p in passed)
        {
            if (firstByHash.TryGetValue(p.Hash, out var first))
            {
                results[p.Index] = Rejected(p.File, p.Name, CvUploadErrorCodes.Duplicate,
                    $"Trùng nội dung với file \"{first.Name}\" trong cùng lần tải.");
            }
            else
            {
                firstByHash[p.Hash] = p;
                unique.Add(p);
            }
        }

        // ---- Bước 4: trùng với CV đã có trong database (một câu truy vấn cho tất cả file, chỉ đọc) ----
        var hashes = unique.Select(p => p.Hash).ToList();
        var existing = hashes.Count == 0
            ? new Dictionary<string, ExistingCv>(StringComparer.OrdinalIgnoreCase)
            : await db.Cvs.AsNoTracking()
                .Where(c => hashes.Contains(c.FileHash))
                .Select(c => new ExistingCv(c.FileHash, c.OriginalFileName, c.UploadedAt))
                .ToDictionaryAsync(c => c.FileHash, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var p in unique)
        {
            if (existing.TryGetValue(p.Hash, out var old))
            {
                // Chỉ báo tên file gốc và ngày tải của CV cũ để HR nhận ra; không trả đường dẫn lưu hay CvId.
                results[p.Index] = Rejected(p.File, p.Name, CvUploadErrorCodes.Duplicate,
                    $"Trùng với CV đã tải trước đó: \"{old.OriginalFileName}\" (ngày {ToLocalDate(old.UploadedAt)}).");
                continue;
            }

            // A6: lưu file rồi ghi database (mỗi file một lần, để file này lỗi không kéo theo file khác)
            results[p.Index] = await SaveAsync(p, source, cancellationToken);
        }

        var failed = results.Count(r => r.Status == CvUploadStatuses.Rejected);
        logger.LogInformation("Nhận {Total} file CV: {Valid} đã lưu, {Failed} bị từ chối",
            results.Length, results.Length - failed, failed);

        // Chỉ ghi vị trí và mã lỗi: tên file và thông báo có thể chứa tên ứng viên (dữ liệu cá nhân).
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i].Status == CvUploadStatuses.Rejected)
            {
                logger.LogInformation("Từ chối file thứ {Position}/{Total}: {Code}", i + 1, results.Length, results[i].ErrorCode);
            }
        }

        return new CvUploadResponse
        {
            TotalFiles = results.Length,
            SuccessCount = results.Length - failed,
            FailedCount = failed,
            Files = [.. results]
        };
    }

    /// <summary>
    /// A6: lưu một file hợp lệ.
    /// Thứ tự: ghi file ra ổ đĩa → thêm dòng Cvs → SaveChanges. Nếu bước nào lỗi thì dọn file vừa ghi,
    /// để không có file "mồ côi" (có trên ổ đĩa mà không có trong database).
    /// </summary>
    private async Task<CvUploadItemResult> SaveAsync(PassedFile p, CvUploadSource source, CancellationToken ct)
    {
        var cvId = Guid.NewGuid();
        var uploadedAt = DateTime.UtcNow;   // dùng cho cả thư mục yyyy/MM và cột UploadedAt
        string? storedPath = null;
        Cv? cv = null;

        try
        {
            await using (var stream = p.File.OpenReadStream())
            {
                // Nếu ghi dở bị lỗi, LocalCvFileStorage tự xóa phần đã ghi
                storedPath = await storage.SaveAsync(cvId, p.Extension, stream, uploadedAt, ct);
            }

            cv = new Cv
            {
                CvId = cvId,
                SourceChannel = source.SourceChannel,
                ConsentConfirmedAt = source.ConsentConfirmedAtUtc,
                CandidateId = null,                 // chưa biết ứng viên; bổ sung khi đọc được tên (A10) hoặc HR nhập (M3)
                OriginalFileName = p.Name,
                StoredPath = storedPath,
                ContentType = p.ContentType,        // loại thật đã nhận diện ở A5
                FileSizeBytes = p.File.Length,
                FileHash = p.Hash,
                ExtractionStatus = ExtractionStatuses.Pending,   // A7: tiến trình nền sẽ đọc chữ
                UploadedAt = uploadedAt
            };

            db.Cvs.Add(cv);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Đã lưu CV {CvId} tại {StoredPath}", cvId, storedPath);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Hai người tải cùng một file cùng lúc: cả hai qua được bước kiểm tra trùng,
            // nhưng ràng buộc UQ_Cvs_FileHash chỉ cho một bản vào database.
            await CleanupAsync(cvId, storedPath, cv);
            logger.LogWarning("CV mới {CvId} trùng hash với CV vừa lưu đồng thời, đã bỏ", cvId);
            return Rejected(p.File, p.Name, CvUploadErrorCodes.Duplicate, "Trùng với một CV vừa được tải lên cùng lúc.");
        }
        catch (OperationCanceledException)
        {
            // Người dùng hủy / ngắt kết nối giữa chừng: dọn file nếu dòng Cvs chưa được lưu, rồi để hủy tiếp tục
            await CleanupAsync(cvId, storedPath, cv);
            throw;
        }
        catch (Exception ex)
        {
            await CleanupAsync(cvId, storedPath, cv);
            logger.LogError(ex, "Không lưu được CV {CvId}", cvId);
            return Rejected(p.File, p.Name, CvUploadErrorCodes.SaveFailed, "Không lưu được file trên máy chủ. Vui lòng thử lại.");
        }

        // A7: xếp vào hàng đợi đọc chữ. Đặt NGOÀI khối try ở trên: CV đã lưu xong, nếu xếp hàng lỗi
        // thì không được dọn file/dòng Cvs. CV vẫn Pending và được worker tự xếp lại khi API khởi động.
        try
        {
            await extractionQueue.EnqueueAsync(cvId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Không xếp được CV {CvId} vào hàng đợi đọc chữ; sẽ xếp lại khi API khởi động", cvId);
        }

        return new CvUploadItemResult
        {
            FileName = p.Name,
            SizeBytes = p.File.Length,
            ContentType = p.ContentType,
            Status = CvUploadStatuses.Accepted,
            CvId = cvId
        };
    }

    /// <summary>
    /// Dọn sau khi lưu lỗi: bỏ dòng Cvs khỏi DbContext và xóa file đã ghi.
    /// Trước khi xóa file, kiểm tra lại database: nếu dòng Cvs thực ra đã được lưu (ví dụ hủy request ngay sau khi
    /// database ghi xong) thì GIỮ file, để database không trỏ tới file không tồn tại.
    /// </summary>
    private async Task CleanupAsync(Guid cvId, string? storedPath, Cv? cv)
    {
        if (cv is not null) db.Entry(cv).State = EntityState.Detached;
        if (storedPath is null) return;   // chưa ghi file (hoặc storage đã tự xóa phần ghi dở)

        try
        {
            if (await db.Cvs.AsNoTracking().AnyAsync(c => c.CvId == cvId, CancellationToken.None))
            {
                logger.LogWarning("CV {CvId} đã có trong database dù lưu báo lỗi; giữ file {StoredPath}", cvId, storedPath);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Không kiểm tra được CV {CvId} trong database; giữ file {StoredPath} để xử lý thủ công", cvId, storedPath);
            return;
        }

        try { storage.Delete(storedPath); }
        catch (Exception ex) { logger.LogError(ex, "Không xóa được file {StoredPath} của CV lưu lỗi; cần xóa thủ công", storedPath); }
    }

    // 2601: trùng chỉ mục UNIQUE; 2627: trùng ràng buộc UNIQUE / khóa chính
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };

    private static CvUploadItemResult Rejected(CvUploadFile file, string name, string code, string message) => new()
    {
        FileName = name,
        SizeBytes = file.Length,
        ContentType = file.ContentType,
        Status = CvUploadStatuses.Rejected,
        ErrorCode = code,
        ErrorMessage = message
    };

    // Database lưu giờ UTC (SYSUTCDATETIME); đổi sang giờ máy chủ để hiển thị ngày
    private static string ToLocalDate(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy");

    private sealed record PassedFile(int Index, CvUploadFile File, string Name, string Extension, string ContentType, string Hash);

    private sealed record ExistingCv(string FileHash, string OriginalFileName, DateTime UploadedAt);
}
