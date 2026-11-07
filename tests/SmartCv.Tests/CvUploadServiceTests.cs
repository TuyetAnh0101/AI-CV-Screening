using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Uploads;

namespace SmartCv.Tests;

/// <summary>
/// CvUploadService ở mức A5: kiểm tra từng file, trùng trong lần tải, trùng database (EF Core InMemory).
/// File hợp lệ vẫn Received và CvId null (A6 mới lưu, đổi thành Accepted).
/// Thay cho các test A4 cũ "không từ chối định dạng" và "không đọc nội dung", vì A5 cố ý làm hai việc đó.
/// </summary>
public class CvUploadServiceTests
{
    private static (CvUploadService Service, SmartCvDbContext Db) Create(CvUploadOptions? options = null)
    {
        var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString("N"));
        var service = new CvUploadService(db, Options.Create(options ?? new CvUploadOptions()), NullLogger<CvUploadService>.Instance);
        return (service, db);
    }

    private static CvUploadFile File(string name, byte[] bytes, string contentType = "application/octet-stream") =>
        new(name, contentType, bytes.Length, () => new MemoryStream(bytes));

    /// <summary>File mà nếu bị mở sẽ làm test thất bại: dùng để chứng minh bước 1 không đọc nội dung.</summary>
    private static CvUploadFile Unreadable(string name, long length) =>
        new(name, "application/pdf", length, () => throw new InvalidOperationException("Không được đọc nội dung file này"));

    [Fact]
    public async Task Valid_pdf_png_jpg_are_received_with_detected_content_type()
    {
        var (service, _) = Create();

        var result = await service.UploadAsync(
        [
            File("cv.pdf", TestSupport.Pdf("a")),
            File("anh.png", TestSupport.Png("b"), "image/png"),
            File("anh.JPG", TestSupport.Jpg("c"), "application/octet-stream"),   // ContentType của client sai
        ]);

        Assert.Equal((3, 3, 0), (result.TotalFiles, result.SuccessCount, result.FailedCount));
        Assert.All(result.Files, f =>
        {
            Assert.Equal(CvUploadStatuses.Received, f.Status);
            Assert.Null(f.CvId);
            Assert.Null(f.ErrorCode);
            Assert.Null(f.ErrorMessage);
        });
        Assert.Equal(["application/pdf", "image/png", "image/jpeg"], result.Files.Select(f => f.ContentType));
    }

    [Theory]
    [InlineData("file-rong.pdf", 0L, CvUploadErrorCodes.EmptyFile)]
    [InlineData("qua-lon.pdf", 12L * 1024 * 1024, CvUploadErrorCodes.TooLarge)]
    [InlineData("cv-word.docx", 100L, CvUploadErrorCodes.InvalidFormat)]
    [InlineData("cv-khong-duoi", 100L, CvUploadErrorCodes.InvalidFormat)]
    public async Task Basic_checks_reject_without_reading_content(string name, long length, string code)
    {
        var (service, _) = Create();

        var result = await service.UploadAsync([Unreadable(name, length)]);

        var item = Assert.Single(result.Files);
        Assert.Equal(CvUploadStatuses.Rejected, item.Status);
        Assert.Equal(code, item.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(item.ErrorMessage));
        Assert.Equal((1, 0, 1), (result.TotalFiles, result.SuccessCount, result.FailedCount));
    }

    [Fact]
    public async Task Fake_and_renamed_files_are_invalid_format()
    {
        var (service, _) = Create();

        var result = await service.UploadAsync(
        [
            File("gia-mao-pdf.pdf", "Day khong phai CV ma la file chu"u8.ToArray(), "application/pdf"),
            File("anh-png-doi-duoi.pdf", TestSupport.Png("x"), "application/pdf"),
        ]);

        Assert.All(result.Files, f => Assert.Equal(CvUploadErrorCodes.InvalidFormat, f.ErrorCode));
        Assert.Contains("Nội dung không phải file PDF", result.Files[0].ErrorMessage);
        Assert.Contains("nội dung là ảnh PNG", result.Files[1].ErrorMessage);
    }

    [Fact]
    public async Task Same_content_different_name_in_one_upload_keeps_first()
    {
        var (service, _) = Create();
        var bytes = TestSupport.Pdf("cung-noi-dung");

        var result = await service.UploadAsync([File("cv-mau-1.pdf", bytes), File("cv-mau-1-ban-sao.pdf", bytes)]);

        Assert.Equal(CvUploadStatuses.Received, result.Files[0].Status);
        Assert.Equal(CvUploadErrorCodes.Duplicate, result.Files[1].ErrorCode);
        Assert.Equal("Trùng nội dung với file \"cv-mau-1.pdf\" trong cùng lần tải.", result.Files[1].ErrorMessage);
    }

    [Fact]
    public async Task Same_name_different_content_is_not_duplicate()
    {
        var (service, _) = Create();

        var result = await service.UploadAsync([File("cv.pdf", TestSupport.Pdf("ban-cu")), File("cv.pdf", TestSupport.Pdf("ban-da-sua"))]);

        Assert.All(result.Files, f => Assert.Equal(CvUploadStatuses.Received, f.Status));
    }

    [Fact]
    public async Task Duplicate_of_cv_in_database_is_rejected_with_original_name_and_date()
    {
        var (service, db) = Create();
        var bytes = TestSupport.Pdf("da-co-trong-database");
        var hash = await FileHasher.ComputeSha256Async(new MemoryStream(bytes));
        db.Cvs.Add(new Cv
        {
            CvId = Guid.NewGuid(), OriginalFileName = "CV_DaCo.pdf", StoredPath = "Uploads/CV/2026/11/x.pdf",
            ContentType = "application/pdf", FileSizeBytes = bytes.Length, FileHash = hash,
            ExtractionStatus = "Pending", UploadedAt = new DateTime(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await service.UploadAsync([File("cv-moi.pdf", bytes), File("khac.pdf", TestSupport.Pdf("khac"))]);

        var dup = result.Files[0];
        Assert.Equal(CvUploadErrorCodes.Duplicate, dup.ErrorCode);
        Assert.StartsWith("Trùng với CV đã tải trước đó: \"CV_DaCo.pdf\" (ngày ", dup.ErrorMessage);
        Assert.DoesNotContain("Uploads", dup.ErrorMessage);   // không lộ đường dẫn lưu
        Assert.Null(dup.CvId);
        Assert.Equal(CvUploadStatuses.Received, result.Files[1].Status);
        Assert.Equal(1, db.Cvs.Count());                     // A5 không ghi gì vào bảng Cvs
    }

    [Fact]
    public async Task Mixed_eight_files_keep_order_and_counts()
    {
        var (service, _) = Create();
        var cv1 = TestSupport.Pdf("cv-mau-1");

        var result = await service.UploadAsync(
        [
            File("cv-mau-1.pdf", cv1),
            File("file-rong.pdf", []),
            File("cv-mau-2.png", TestSupport.Png("cv-mau-2")),
            File("cv-word.docx", "PK"u8.ToArray()),
            File("gia-mao-pdf.pdf", "Day khong phai CV"u8.ToArray()),
            File("cv-mau-4.jpg", TestSupport.Jpg("cv-mau-4")),
            File("anh-png-doi-duoi.pdf", TestSupport.Png("doi-duoi")),
            File("cv-mau-1-ban-sao.pdf", cv1),
        ]);

        Assert.Equal((8, 3, 5), (result.TotalFiles, result.SuccessCount, result.FailedCount));
        Assert.Equal(
            ["cv-mau-1.pdf", "file-rong.pdf", "cv-mau-2.png", "cv-word.docx", "gia-mao-pdf.pdf", "cv-mau-4.jpg", "anh-png-doi-duoi.pdf", "cv-mau-1-ban-sao.pdf"],
            result.Files.Select(f => f.FileName));
        Assert.Equal(
            [null, CvUploadErrorCodes.EmptyFile, null, CvUploadErrorCodes.InvalidFormat, CvUploadErrorCodes.InvalidFormat, null, CvUploadErrorCodes.InvalidFormat, CvUploadErrorCodes.Duplicate],
            result.Files.Select(f => f.ErrorCode));
    }

    [Theory]
    [InlineData(@"C:\Users\hr\Desktop\cv.pdf", "cv.pdf")]
    [InlineData("../../etc/cv.pdf", "cv.pdf")]
    [InlineData(@"..\..\cv.pdf", "cv.pdf")]
    [InlineData("Nguyễn Văn An - CV.pdf", "Nguyễn Văn An - CV.pdf")]
    public async Task File_name_is_stripped_of_client_path(string sent, string expected)
    {
        var (service, _) = Create();

        var result = await service.UploadAsync([File(sent, TestSupport.Pdf(sent))]);

        Assert.Equal(expected, Assert.Single(result.Files).FileName);
    }

    [Fact]
    public async Task Empty_list_returns_zero_counts()
    {
        var (service, _) = Create();

        var result = await service.UploadAsync([]);

        Assert.Equal((0, 0, 0), (result.TotalFiles, result.SuccessCount, result.FailedCount));
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task Cancelled_request_stops_processing()
    {
        var (service, _) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.UploadAsync([File("cv.pdf", TestSupport.Pdf("a"))], cts.Token));
    }

    [Fact]
    public void Default_options_match_upload_limits()
    {
        var options = new CvUploadOptions();

        Assert.Equal(10, options.MaxFileSizeMB);
        Assert.Equal(20, options.MaxFilesPerRequest);
        Assert.Equal([".pdf", ".png", ".jpg", ".jpeg"], options.AllowedExtensions);
        Assert.Equal("Uploads/CV", options.StorageRoot);
        Assert.Equal(10L * 1024 * 1024, options.MaxFileSizeBytes);
        Assert.Equal(201L * 1024 * 1024, options.MaxRequestBytes);   // 20 × 10 MiB + 1 MiB
    }
}
