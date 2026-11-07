using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Api.Background;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Extraction;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Extraction;

namespace SmartCv.Tests;

/// <summary>A7: ghi kết quả đọc chữ vào bảng Cvs, và tiến trình nền tự xếp lại CV Pending khi khởi động.</summary>
public class CvExtractionProcessorTests
{
    private static async Task<(SmartCvDbContext Db, ICvFileStorage Storage, Cv Cv)> SeedAsync(
        string dbName, string status = ExtractionStatuses.Pending, bool writeFile = true)
    {
        var db = TestSupport.InMemoryDb(dbName);
        var storage = TestSupport.Storage(TestSupport.TempRoot());
        var id = Guid.NewGuid();
        var uploadedAt = DateTime.UtcNow;
        var storedPath = writeFile
            ? await storage.SaveAsync(id, ".pdf", new MemoryStream(TestSupport.Pdf("p")), uploadedAt)
            : $"Uploads/CV/2026/11/{id}.pdf";
        var cv = new Cv
        {
            CvId = id, OriginalFileName = "Nguyễn Văn An.pdf", StoredPath = storedPath, ContentType = "application/pdf",
            FileSizeBytes = 10, FileHash = Guid.NewGuid().ToString("N").PadRight(64, '0').ToUpperInvariant(),
            ExtractionStatus = status, UploadedAt = uploadedAt
        };
        db.Cvs.Add(cv);
        await db.SaveChangesAsync();
        return (db, storage, cv);
    }

    private static CvExtractionProcessor Processor(SmartCvDbContext db, ICvFileStorage storage, ICvTextExtractor extractor,
        ILogger<CvExtractionProcessor>? logger = null) =>
        new(db, storage, extractor, logger ?? NullLogger<CvExtractionProcessor>.Instance);

    [Fact]
    public async Task Success_saves_text_method_and_completed()
    {
        var (db, storage, cv) = await SeedAsync(Guid.NewGuid().ToString("N"));
        var extractor = new FakeExtractor(TextExtractionResult.Success("Kỹ năng: C#, SQL", ExtractionMethods.PdfText, 1));

        await Processor(db, storage, extractor).ProcessAsync(cv.CvId, default);

        var saved = await db.Cvs.AsNoTracking().SingleAsync();
        Assert.Equal(ExtractionStatuses.Completed, saved.ExtractionStatus);
        Assert.Equal(ExtractionMethods.PdfText, saved.ExtractionMethod);
        Assert.Equal("Kỹ năng: C#, SQL", saved.ExtractedText);
        Assert.Null(saved.ExtractionError);
        Assert.Equal("application/pdf", extractor.LastContentType);
    }

    [Fact]
    public async Task Failure_saves_error_and_failed_and_truncates_long_errors()
    {
        var (db, storage, cv) = await SeedAsync(Guid.NewGuid().ToString("N"));
        var extractor = new FakeExtractor(TextExtractionResult.Failure(new string('x', 1500), ExtractionMethods.ImageOcr));

        await Processor(db, storage, extractor).ProcessAsync(cv.CvId, default);

        var saved = await db.Cvs.AsNoTracking().SingleAsync();
        Assert.Equal(ExtractionStatuses.Failed, saved.ExtractionStatus);
        Assert.Equal(ExtractionMethods.ImageOcr, saved.ExtractionMethod);
        Assert.Null(saved.ExtractedText);
        Assert.Equal(1000, saved.ExtractionError!.Length);      // vừa cột NVARCHAR(1000)
    }

    [Fact]
    public async Task Missing_file_fails_without_calling_extractor()
    {
        var (db, storage, cv) = await SeedAsync(Guid.NewGuid().ToString("N"), writeFile: false);
        var extractor = new FakeExtractor(TextExtractionResult.Success("x", ExtractionMethods.PdfText, 1));

        await Processor(db, storage, extractor).ProcessAsync(cv.CvId, default);

        var saved = await db.Cvs.AsNoTracking().SingleAsync();
        Assert.Equal(ExtractionStatuses.Failed, saved.ExtractionStatus);
        Assert.Equal(CvExtractionProcessor.FileMissingError, saved.ExtractionError);
        Assert.Equal(0, extractor.Calls);
    }

    [Fact]
    public async Task Unexpected_exception_is_stored_without_exception_message()
    {
        var (db, storage, cv) = await SeedAsync(Guid.NewGuid().ToString("N"));
        var extractor = new FakeExtractor(error: new IOException(@"C:\bi-mat\duong-dan\noi-bo.dll lỗi"));

        await Processor(db, storage, extractor).ProcessAsync(cv.CvId, default);

        var saved = await db.Cvs.AsNoTracking().SingleAsync();
        Assert.Equal(ExtractionStatuses.Failed, saved.ExtractionStatus);
        Assert.DoesNotContain("bi-mat", saved.ExtractionError);
    }

    [Theory]
    [InlineData(ExtractionStatuses.Completed)]
    [InlineData(ExtractionStatuses.Failed)]
    public async Task Non_pending_cv_is_skipped(string status)
    {
        var (db, storage, cv) = await SeedAsync(Guid.NewGuid().ToString("N"), status);
        var extractor = new FakeExtractor(TextExtractionResult.Success("x", ExtractionMethods.PdfText, 1));

        await Processor(db, storage, extractor).ProcessAsync(cv.CvId, default);

        Assert.Equal(0, extractor.Calls);
        Assert.Equal(status, (await db.Cvs.AsNoTracking().SingleAsync()).ExtractionStatus);
    }

    [Fact]
    public async Task Logs_do_not_contain_file_name_or_text()
    {
        var (db, storage, cv) = await SeedAsync(Guid.NewGuid().ToString("N"));
        var logger = new ListLogger<CvExtractionProcessor>();
        var extractor = new FakeExtractor(TextExtractionResult.Success("Nội dung bí mật của ứng viên", ExtractionMethods.PdfText, 1));

        await Processor(db, storage, extractor, logger).ProcessAsync(cv.CvId, default);

        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, m =>
        {
            Assert.DoesNotContain("Nguyễn", m);
            Assert.DoesNotContain("bí mật", m);
        });
    }

    [Fact]
    public async Task Worker_requeues_pending_cvs_on_start_and_processes_them()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var root = TestSupport.TempRoot();
        var storage = TestSupport.Storage(root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SmartCvDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<ICvFileStorage>(storage);
        services.AddSingleton<ICvExtractionQueue, CvExtractionQueue>();
        services.AddSingleton<ICvTextExtractor>(new FakeExtractor(TextExtractionResult.Success("ok", ExtractionMethods.PdfText, 1)));
        services.AddScoped<CvExtractionProcessor>();
        services.AddSingleton<CvExtractionWorker>();
        await using var sp = services.BuildServiceProvider();

        // Giống lần trước tắt API khi còn CV chờ: 2 CV Pending + 1 CV đã xong
        var ids = new List<Guid>();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            foreach (var status in new[] { ExtractionStatuses.Pending, ExtractionStatuses.Pending, ExtractionStatuses.Completed })
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.Cvs.Add(new Cv
                {
                    CvId = id, OriginalFileName = "a.pdf", ContentType = "application/pdf", FileSizeBytes = 1,
                    StoredPath = await storage.SaveAsync(id, ".pdf", new MemoryStream([1]), DateTime.UtcNow),
                    FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = status,
                    ExtractedText = status == ExtractionStatuses.Completed ? "cũ" : null, UploadedAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }

        var worker = sp.GetRequiredService<CvExtractionWorker>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
                if (!await db.Cvs.AnyAsync(c => c.ExtractionStatus == ExtractionStatuses.Pending)) break;
                await Task.Delay(50);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        using (var scope = sp.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().Cvs.AsNoTracking().ToListAsync();
            Assert.All(rows, r => Assert.Equal(ExtractionStatuses.Completed, r.ExtractionStatus));
            Assert.Equal("cũ", rows.Single(r => r.CvId == ids[2]).ExtractedText);   // CV đã xong không bị đọc lại
            Assert.Equal(2, ((FakeExtractor)sp.GetRequiredService<ICvTextExtractor>()).Calls);
        }
    }

    private sealed class FakeExtractor(TextExtractionResult? result = null, Exception? error = null) : ICvTextExtractor
    {
        private int _calls;
        public int Calls => _calls;
        public string? LastContentType { get; private set; }

        public Task<TextExtractionResult> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            LastContentType = contentType;
            if (error is not null) throw error;
            return Task.FromResult(result!);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
