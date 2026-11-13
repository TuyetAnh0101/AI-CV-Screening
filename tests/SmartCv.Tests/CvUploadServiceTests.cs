using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Extraction;
using SmartCv.Infrastructure.Uploads;

namespace SmartCv.Tests;

/// <summary>
/// CvUploadService: kiểm tra từng file (A5) và lưu file hợp lệ (A6) – database EF Core InMemory,
/// file lưu vào thư mục tạm. File hợp lệ trả Accepted + CvId, có file trên ổ đĩa và dòng Cvs (Pending).
/// </summary>
public class CvUploadServiceTests
{
    private sealed record Ctx(CvUploadService Service, SmartCvDbContext Db, string Root, ICvFileStorage Storage, RecordingQueue Queue)
    {
        public Task<CvUploadResponse> UploadAsync(IReadOnlyList<CvUploadFile> files, CancellationToken ct = default) =>
            Service.UploadAsync(files, A38TestData.ConfirmedSource(), ct);
        public string[] SavedFiles() =>
            Directory.Exists(Path.Combine(Root, "Uploads")) ? Directory.GetFiles(Path.Combine(Root, "Uploads"), "*", SearchOption.AllDirectories) : [];
    }

    private static Ctx Create(SmartCvDbContext? db = null, ICvFileStorage? storage = null, bool queueFails = false)
    {
        db ??= TestSupport.InMemoryDb(Guid.NewGuid().ToString("N"));
        var root = TestSupport.TempRoot();
        storage ??= TestSupport.Storage(root);
        var queue = new RecordingQueue(queueFails);
        var service = new CvUploadService(db, storage, queue, Options.Create(new CvUploadOptions()), NullLogger<CvUploadService>.Instance);
        return new Ctx(service, db, root, storage, queue);
    }

    private static CvUploadFile File(string name, byte[] bytes, string contentType = "application/octet-stream") =>
        new(name, contentType, bytes.Length, () => new MemoryStream(bytes));

    /// <summary>File mà nếu bị mở sẽ làm test thất bại: dùng để chứng minh bước 1 không đọc nội dung.</summary>
    private static CvUploadFile Unreadable(string name, long length) =>
        new(name, "application/pdf", length, () => throw new InvalidOperationException("Không được đọc nội dung file này"));

    // ---------- A5: kiểm tra ----------

    [Theory]
    [InlineData("file-rong.pdf", 0L, CvUploadErrorCodes.EmptyFile)]
    [InlineData("qua-lon.pdf", 12L * 1024 * 1024, CvUploadErrorCodes.TooLarge)]
    [InlineData("cv-word.docx", 100L, CvUploadErrorCodes.InvalidFormat)]
    [InlineData("cv-khong-duoi", 100L, CvUploadErrorCodes.InvalidFormat)]
    public async Task Basic_checks_reject_without_reading_content(string name, long length, string code)
    {
        var ctx = Create();

        var result = await ctx.UploadAsync([Unreadable(name, length)]);

        var item = Assert.Single(result.Files);
        Assert.Equal(CvUploadStatuses.Rejected, item.Status);
        Assert.Equal(code, item.ErrorCode);
        Assert.Null(item.CvId);
        Assert.Equal((1, 0, 1), (result.TotalFiles, result.SuccessCount, result.FailedCount));
        Assert.Empty(ctx.SavedFiles());
        Assert.Equal(0, ctx.Db.Cvs.Count());
    }

    [Fact]
    public async Task Fake_and_renamed_files_are_invalid_format()
    {
        var ctx = Create();

        var result = await ctx.UploadAsync(
        [
            File("gia-mao-pdf.pdf", "Day khong phai CV ma la file chu"u8.ToArray(), "application/pdf"),
            File("anh-png-doi-duoi.pdf", TestSupport.Png("x"), "application/pdf"),
        ]);

        Assert.All(result.Files, f => Assert.Equal(CvUploadErrorCodes.InvalidFormat, f.ErrorCode));
        Assert.Contains("Nội dung không phải file PDF", result.Files[0].ErrorMessage);
        Assert.Contains("nội dung là ảnh PNG", result.Files[1].ErrorMessage);
        Assert.Empty(ctx.SavedFiles());
    }

    [Fact]
    public async Task Same_content_different_name_in_one_upload_keeps_first()
    {
        var ctx = Create();
        var bytes = TestSupport.Pdf("cung-noi-dung");

        var result = await ctx.UploadAsync([File("cv-mau-1.pdf", bytes), File("cv-mau-1-ban-sao.pdf", bytes)]);

        Assert.Equal(CvUploadStatuses.Accepted, result.Files[0].Status);
        Assert.Equal(CvUploadErrorCodes.Duplicate, result.Files[1].ErrorCode);
        Assert.Equal("Trùng nội dung với file \"cv-mau-1.pdf\" trong cùng lần tải.", result.Files[1].ErrorMessage);
        Assert.Single(ctx.SavedFiles());
        Assert.Equal(1, ctx.Db.Cvs.Count());
    }

    [Fact]
    public async Task Same_name_different_content_is_not_duplicate()
    {
        var ctx = Create();

        var result = await ctx.UploadAsync([File("cv.pdf", TestSupport.Pdf("ban-cu")), File("cv.pdf", TestSupport.Pdf("ban-da-sua"))]);

        Assert.All(result.Files, f => Assert.Equal(CvUploadStatuses.Accepted, f.Status));
        Assert.Equal(2, ctx.SavedFiles().Length);   // tên vật lý là GUID nên hai file cùng tên gốc không đè nhau
    }

    [Fact]
    public async Task Duplicate_of_cv_in_database_is_rejected_with_original_name_and_date()
    {
        var ctx = Create();
        var bytes = TestSupport.Pdf("da-co-trong-database");
        var hash = await FileHasher.ComputeSha256Async(new MemoryStream(bytes));
        ctx.Db.Cvs.Add(new Cv
        {
            CvId = Guid.NewGuid(), OriginalFileName = "CV_DaCo.pdf", StoredPath = "Uploads/CV/2026/11/x.pdf",
            ContentType = "application/pdf", FileSizeBytes = bytes.Length, FileHash = hash,
            ExtractionStatus = "Pending", UploadedAt = new DateTime(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc)
        });
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var result = await ctx.UploadAsync([File("cv-moi.pdf", bytes), File("khac.pdf", TestSupport.Pdf("khac"))]);

        var dup = result.Files[0];
        Assert.Equal(CvUploadErrorCodes.Duplicate, dup.ErrorCode);
        Assert.StartsWith("Trùng với CV đã tải trước đó: \"CV_DaCo.pdf\" (ngày ", dup.ErrorMessage);
        Assert.DoesNotContain("Uploads", dup.ErrorMessage);   // không lộ đường dẫn lưu
        Assert.Null(dup.CvId);
        Assert.Equal(CvUploadStatuses.Accepted, result.Files[1].Status);
        Assert.Equal(2, ctx.Db.Cvs.Count());                  // dòng cũ + file "khac.pdf"
    }

    [Fact]
    public async Task Mixed_eight_files_keep_order_and_counts()
    {
        var ctx = Create();
        var cv1 = TestSupport.Pdf("cv-mau-1");

        var result = await ctx.UploadAsync(
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
        Assert.Equal(3, ctx.SavedFiles().Length);
        Assert.Equal(3, ctx.Db.Cvs.Count());
    }

    // ---------- A6: lưu file và database ----------

    [Fact]
    public async Task Valid_files_are_saved_on_disk_and_in_database()
    {
        var ctx = Create();
        var pdf = TestSupport.Pdf("cv-luu");
        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = await ctx.UploadAsync(
        [
            File(@"C:\Users\hr\Desktop\Nguyễn Văn An CV.PDF", pdf, "application/octet-stream"),
            File("anh.JPG", TestSupport.Jpg("anh")),
        ]);

        Assert.Equal((2, 2, 0), (result.TotalFiles, result.SuccessCount, result.FailedCount));
        Assert.All(result.Files, f =>
        {
            Assert.Equal(CvUploadStatuses.Accepted, f.Status);
            Assert.NotNull(f.CvId);
            Assert.NotEqual(Guid.Empty, f.CvId);
            Assert.Null(f.ErrorCode);
        });

        var item = result.Files[0];
        var cv = await ctx.Db.Cvs.AsNoTracking().SingleAsync(c => c.CvId == item.CvId);
        Assert.Null(cv.CandidateId);
        Assert.Equal("Nguyễn Văn An CV.PDF", cv.OriginalFileName);    // tên gốc (đã bỏ đường dẫn) chỉ lưu trong database
        Assert.Equal("application/pdf", cv.ContentType);                // loại thật, không phải loại client gửi
        Assert.Equal(pdf.Length, cv.FileSizeBytes);
        Assert.Equal(await FileHasher.ComputeSha256Async(new MemoryStream(pdf)), cv.FileHash);
        Assert.Equal(ExtractionStatuses.Pending, cv.ExtractionStatus);
        Assert.InRange(cv.UploadedAt, before, DateTime.UtcNow.AddSeconds(1));

        // Tên vật lý là GUID + đuôi viết thường, trong thư mục yyyy/MM; StoredPath dùng dấu '/'
        Assert.Equal($"Uploads/CV/{cv.UploadedAt:yyyy}/{cv.UploadedAt:MM}/{cv.CvId}.pdf", cv.StoredPath);
        Assert.True(ctx.Storage.Exists(cv.StoredPath));
        await using (var saved = ctx.Storage.OpenRead(cv.StoredPath))
        using (var copy = new MemoryStream())
        {
            await saved.CopyToAsync(copy);
            Assert.Equal(pdf, copy.ToArray());
        }

        var jpg = await ctx.Db.Cvs.AsNoTracking().SingleAsync(c => c.CvId == result.Files[1].CvId);
        Assert.EndsWith($"{jpg.CvId}.jpg", jpg.StoredPath);
    }

    [Fact]
    public async Task Storage_failure_returns_save_failed_without_database_row_and_keeps_other_files()
    {
        var root = TestSupport.TempRoot();
        var ctx = Create(storage: new FailingStorage(TestSupport.Storage(root), failOnCall: 1));

        var result = await ctx.UploadAsync([File("loi.pdf", TestSupport.Pdf("loi")), File("ok.pdf", TestSupport.Pdf("ok"))]);

        Assert.Equal(CvUploadStatuses.Rejected, result.Files[0].Status);
        Assert.Equal(CvUploadErrorCodes.SaveFailed, result.Files[0].ErrorCode);
        Assert.Equal("Không lưu được file trên máy chủ. Vui lòng thử lại.", result.Files[0].ErrorMessage);
        Assert.Equal(CvUploadStatuses.Accepted, result.Files[1].Status);   // file lỗi không kéo theo file khác
        Assert.Equal((2, 1, 1), (result.TotalFiles, result.SuccessCount, result.FailedCount));
        Assert.Equal(1, ctx.Db.Cvs.Count());
    }

    [Fact]
    public async Task Database_failure_deletes_saved_file_and_returns_save_failed()
    {
        var db = new FailingSaveDb(Guid.NewGuid().ToString("N"));
        var ctx = Create(db: db);

        var result = await ctx.UploadAsync([File("cv.pdf", TestSupport.Pdf("db-loi"))]);

        Assert.Equal(CvUploadErrorCodes.SaveFailed, Assert.Single(result.Files).ErrorCode);
        Assert.Empty(ctx.SavedFiles());       // file đã ghi được xóa: không có file mồ côi
        Assert.Equal(0, db.Cvs.Count());
    }

    [Fact]
    public async Task Cancelled_request_stops_processing_and_leaves_no_file()
    {
        var ctx = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ctx.UploadAsync([File("cv.pdf", TestSupport.Pdf("a"))], cts.Token));
        Assert.Empty(ctx.SavedFiles());
    }

    [Theory]
    [InlineData(@"C:\Users\hr\Desktop\cv.pdf", "cv.pdf")]
    [InlineData("../../etc/cv.pdf", "cv.pdf")]
    [InlineData(@"..\..\cv.pdf", "cv.pdf")]
    public async Task File_name_is_stripped_of_client_path(string sent, string expected)
    {
        var ctx = Create();

        var result = await ctx.UploadAsync([File(sent, TestSupport.Pdf(sent))]);

        Assert.Equal(expected, Assert.Single(result.Files).FileName);
        Assert.All(ctx.SavedFiles(), f => Assert.StartsWith(Path.Combine(ctx.Root, "Uploads", "CV"), f));
    }

    [Fact]
    public async Task Empty_list_returns_zero_counts()
    {
        var ctx = Create();

        var result = await ctx.UploadAsync([]);

        Assert.Equal((0, 0, 0), (result.TotalFiles, result.SuccessCount, result.FailedCount));
    }

    [Fact]
    public void Default_options_match_upload_limits()
    {
        var options = new CvUploadOptions();

        Assert.Equal(10, options.MaxFileSizeMB);
        Assert.Equal(20, options.MaxFilesPerRequest);
        Assert.Equal([".pdf", ".png", ".jpg", ".jpeg"], options.AllowedExtensions);
        Assert.Equal("Uploads/CV", options.StorageRoot);
        Assert.Equal(201L * 1024 * 1024, options.MaxRequestBytes);
    }

    /// <summary>Storage báo lỗi ở lần lưu thứ <c>failOnCall</c>, các lần khác chuyển cho storage thật.</summary>
    // ---------- A7: xếp CV vào hàng đợi đọc chữ ----------

    [Fact]
    public async Task Only_saved_cvs_are_enqueued_for_extraction_in_order()
    {
        var ctx = Create();

        var result = await ctx.UploadAsync([
            File("a.pdf", TestSupport.Pdf("q-a")),
            File("rong.pdf", []),
            File("b.png", TestSupport.Png("q-b")),
            File("a-trung.pdf", TestSupport.Pdf("q-a"))
        ]);

        Assert.Equal([result.Files[0].CvId!.Value, result.Files[2].CvId!.Value], ctx.Queue.Items);
    }

    [Fact]
    public async Task Save_failure_is_not_enqueued()
    {
        var ctx = Create(storage: new FailingStorage(TestSupport.Storage(TestSupport.TempRoot()), failOnCall: 1));

        var result = await ctx.UploadAsync([File("a.pdf", TestSupport.Pdf("q-fail"))]);

        Assert.Equal(CvUploadErrorCodes.SaveFailed, result.Files[0].ErrorCode);
        Assert.Empty(ctx.Queue.Items);
    }

    [Fact]
    public async Task Queue_failure_keeps_saved_cv_and_file()
    {
        // Xếp hàng lỗi sau khi đã lưu: KHÔNG được xóa file/dòng Cvs; CV vẫn Pending, worker xếp lại khi khởi động
        var ctx = Create(queueFails: true);

        var result = await ctx.UploadAsync([File("a.pdf", TestSupport.Pdf("q-queue-fail"))]);

        Assert.Equal(CvUploadStatuses.Accepted, result.Files[0].Status);
        var cv = await ctx.Db.Cvs.AsNoTracking().SingleAsync();
        Assert.Equal(ExtractionStatuses.Pending, cv.ExtractionStatus);
        Assert.True(ctx.Storage.Exists(cv.StoredPath));
    }

    [Fact]
    public async Task Real_queue_ignores_duplicate_ids_until_dequeued()
    {
        var queue = new CvExtractionQueue();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await queue.EnqueueAsync(a);
        await queue.EnqueueAsync(a);
        await queue.EnqueueAsync(b);
        Assert.Equal(2, queue.Count);

        using var cts = new CancellationTokenSource();
        var seen = new List<Guid>();
        await foreach (var id in queue.DequeueAllAsync(cts.Token))
        {
            seen.Add(id);
            if (seen.Count == 2) break;
        }
        Assert.Equal([a, b], seen);

        await queue.EnqueueAsync(a);   // đã lấy ra rồi thì được xếp lại (đọc lại)
        Assert.Equal(1, queue.Count);
    }

    private sealed class RecordingQueue(bool fail) : ICvExtractionQueue
    {
        public List<Guid> Items { get; } = [];

        public ValueTask EnqueueAsync(Guid cvId, CancellationToken cancellationToken = default)
        {
            if (fail) throw new InvalidOperationException("Hàng đợi lỗi (giả lập)");
            Items.Add(cvId);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FailingStorage(ICvFileStorage inner, int failOnCall) : ICvFileStorage
    {
        private int _calls;

        public Task<string> SaveAsync(Guid cvId, string extension, Stream content, DateTime uploadedAtUtc, CancellationToken cancellationToken = default) =>
            ++_calls == failOnCall
                ? throw new IOException("Ổ đĩa đầy (giả lập)")
                : inner.SaveAsync(cvId, extension, content, uploadedAtUtc, cancellationToken);

        public Stream OpenRead(string storedPath) => inner.OpenRead(storedPath);
        public bool Exists(string storedPath) => inner.Exists(storedPath);
        public void Delete(string storedPath) => inner.Delete(storedPath);
    }

    /// <summary>DbContext báo lỗi khi SaveChanges (giả lập mất kết nối database).</summary>
    private sealed class FailingSaveDb(string name) : SmartCvDbContext(
        new DbContextOptionsBuilder<SmartCvDbContext>().UseInMemoryDatabase(name).Options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Mất kết nối database (giả lập)");
    }
}
