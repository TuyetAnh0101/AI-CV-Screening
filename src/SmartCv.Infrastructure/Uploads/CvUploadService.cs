using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Uploads;

/// <summary>
/// Xử lý file CV tải lên.
/// A4: nhận file. A5: kiểm tra rỗng, dung lượng, đuôi file, nội dung thật, trùng hash (trong lần tải và trong database).
/// A6 (sau): lưu file hợp lệ vào thư mục và bảng Cvs (xem chỗ TODO A6).
/// Hợp đồng API không đổi: file hợp lệ trả Received, file bị từ chối trả Rejected kèm ErrorCode, ErrorMessage.
/// </summary>
public sealed class CvUploadService(
    SmartCvDbContext db,
    IOptions<CvUploadOptions> options,
    ILogger<CvUploadService> logger) : ICvUploadService
{
    private readonly CvUploadOptions _options = options.Value;

    public async Task<CvUploadResponse> UploadAsync(IReadOnlyList<CvUploadFile> files, CancellationToken cancellationToken = default)
    {
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

            // TODO A6: lưu file vào Uploads/CV/yyyy/MM/<GUID><đuôi>, thêm dòng vào bảng Cvs
            //          (FileHash = p.Hash, ContentType = p.ContentType), trả CvId và Status = Accepted.
            //          Bắt lỗi vi phạm UQ_Cvs_FileHash (hai người tải cùng file cùng lúc) và trả Duplicate.
            results[p.Index] = new CvUploadItemResult
            {
                FileName = p.Name,
                SizeBytes = p.File.Length,
                ContentType = p.ContentType,          // loại file thật, đã nhận diện từ nội dung
                Status = CvUploadStatuses.Received    // A5: hợp lệ, chưa lưu
            };
        }

        var failed = results.Count(r => r.Status == CvUploadStatuses.Rejected);
        logger.LogInformation("Nhận {Total} file CV: {Valid} hợp lệ, {Failed} bị từ chối",
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
