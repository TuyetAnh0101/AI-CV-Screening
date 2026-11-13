using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Api.Background;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Uploads;

namespace SmartCv.Tests;

/// <summary>
/// A42 trên SQL Server thật (opt-in): catalog MỚI SmartCv_A42_Test_&lt;GUID&gt; tạo bằng migration, dữ liệu tổng hợp, giữ lại (không DROP);
/// tên ghi vào artifacts/a42-tests/retained-sql-databases.txt. Không bao giờ dùng SmartCvDb. Không gọi OCR, không gọi Gemini.
/// </summary>
public class A42SqlTests
{
    [A42SqlFact]
    public async Task Two_simultaneous_uploads_of_one_file_keep_one_cv_and_report_the_winner()
    {
        var connection = await A42SqlFactAttribute.NewDatabaseAsync();
        var storage = new BarrierStorage(TestSupport.Storage(TestSupport.TempRoot()), participants: 2);
        var bytes = TestSupport.Pdf("a42-dong-thoi-" + Guid.NewGuid());

        async Task<CvUploadItemResult> UploadAsync()
        {
            await using var db = A42SqlFactAttribute.Db(connection);
            var service = new CvUploadService(db, storage, new NoopQueue(), Options.Create(new CvUploadOptions()), NullLogger<CvUploadService>.Instance);
            var result = await service.UploadAsync([new CvUploadFile("cv.pdf", "application/pdf", bytes.Length, () => new MemoryStream(bytes))],
                A38TestData.ConfirmedSource());
            return result.Files.Single();
        }

        // Cả hai cùng qua bước kiểm tra trùng rồi mới ghi file / database: ràng buộc UQ_Cvs_FileHash quyết định bên thắng
        var results = await Task.WhenAll(Task.Run(UploadAsync), Task.Run(UploadAsync));

        var accepted = Assert.Single(results, r => r.Status == CvUploadStatuses.Accepted);
        var duplicate = Assert.Single(results, r => r.Status == CvUploadStatuses.Rejected);
        Assert.Equal(CvUploadErrorCodes.Duplicate, duplicate.ErrorCode);
        Assert.Equal("Trùng với một CV vừa được tải lên cùng lúc.", duplicate.ErrorMessage);
        Assert.Equal(accepted.CvId, duplicate.ExistingCvId);
        Assert.Equal("cv.pdf", duplicate.ExistingFileName);
        Assert.Equal(DateTimeKind.Utc, duplicate.ExistingUploadedAt!.Value.Kind);
        Assert.Empty(duplicate.ExistingJobTitles);
        await using var check = A42SqlFactAttribute.Db(connection);
        Assert.Equal(1, await check.Cvs.CountAsync());
        Assert.Single(storage.SavedPaths, storage.Exists);   // file của bên thua đã được dọn
    }

    [A42SqlFact]
    public async Task Duplicate_metadata_text_quality_and_estimate_translate_on_sql_server()
    {
        var connection = await A42SqlFactAttribute.NewDatabaseAsync();
        var bytes = TestSupport.Pdf("a42-sql-" + Guid.NewGuid());
        var hash = await FileHasher.ComputeSha256Async(new MemoryStream(bytes));
        var day = DateTime.UtcNow.AddDays(-3);
        Guid oldId;
        await using (var db = A42SqlFactAttribute.Db(connection))
        {
            var prompt = new PromptVersion { VersionName = "a42-sql", SystemPrompt = "Prompt A42", IsActive = true, CreatedAt = day };
            var cv = new Cv
            {
                CvId = Guid.NewGuid(), OriginalFileName = "CV_TongHop_A42.pdf", StoredPath = "synthetic/a42", ContentType = "application/pdf",
                FileSizeBytes = bytes.Length, FileHash = hash, ExtractionStatus = ExtractionStatuses.Completed, ExtractionMethod = ExtractionMethods.PdfText,
                ExtractedText = string.Join("\n", Enumerable.Repeat("Kinh nghiệm C# và ASP.NET Core, xây dựng API với SQL Server và Docker.", 12)),
                UploadedAt = day, SourceChannel = CvSourceChannels.Referral, ConsentConfirmedAt = day,
                Applications = [new Application { JobId = 2, CreatedAt = day }, new Application { JobId = 1, CreatedAt = day }]
            };
            db.AddRange(prompt, cv);
            await db.SaveChangesAsync();
            oldId = cv.CvId;
            // 3 lượt Screening xong: 10 CV / 70 giây → 7 giây/CV; thêm một lượt PromptTest bị bỏ qua
            var set = new EvaluationSet { Name = "Bộ A42 SQL", CreatedAt = day };
            db.Add(set);
            await db.SaveChangesAsync();
            db.EvaluationRuns.AddRange(Enumerable.Range(0, 3).Select(i => Run(prompt.PromptVersionId, RunTypes.Screening, null, 10, day.AddHours(i), 70))
                .Append(Run(prompt.PromptVersionId, RunTypes.PromptTest, set.EvaluationSetId, 1, day.AddHours(5), 5_000)));
            await db.SaveChangesAsync();
        }

        await using var api = A42SqlFactAttribute.Api(connection);
        using var http = api.CreateClient();
        using var content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "files", "cv-moi.pdf" } };
        using var upload = await http.PostAsync("/api/cvs/upload", A38TestData.WithConsent(content));
        var dup = (await upload.Content.ReadFromJsonAsync<CvUploadResponse>())!.Files.Single();
        using var text = await http.GetAsync($"/api/cvs/{oldId}/text");
        var textDto = (await text.Content.ReadFromJsonAsync<CvTextDto>())!;
        var detail = (await http.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{oldId}"))!;
        var estimate = (await http.GetFromJsonAsync<RunEstimateDto>("/api/runs/estimate?count=10"))!;

        Assert.Equal(oldId, dup.ExistingCvId);
        Assert.Equal(2, dup.ExistingJobTitles.Count);
        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        Assert.Equal("no-store", text.Headers.CacheControl?.ToString());   // khóa đọc A39 trên SQL Server
        Assert.Equal(TextQualityLevels.Good, textDto.Quality!.Level);
        Assert.Equal(TextQualityLevels.Good, detail.TextQuality!.Level);
        Assert.Equal(CvSourceChannels.Referral, detail.SourceChannel);
        Assert.Equal((7m, 3, 70), (estimate.SecondsPerCv, estimate.BasedOnRuns, estimate.EstimatedSeconds));
        await using var check = A42SqlFactAttribute.Db(connection);
        Assert.Equal(1, await check.Cvs.CountAsync());
    }

    private static EvaluationRun Run(int prompt, string type, int? setId, int items, DateTime created, double seconds) => new()
    {
        RunType = type, PromptVersionId = prompt, EvaluationSetId = setId, ModelName = "gemini-test", Temperature = 0.2m, KeywordWeight = 0.3m,
        AiWeight = 0.7m, PassThreshold = 70m, Status = RunStatuses.Completed, TotalItems = items, ProcessedItems = items,
        CreatedAt = created, CompletedAt = created.AddSeconds(seconds)
    };

    /// <summary>Lưu file thật, nhưng chặn cho tới khi đủ <c>participants</c> request cùng tới bước lưu (sau bước kiểm tra trùng).</summary>
    private sealed class BarrierStorage(ICvFileStorage inner, int participants) : ICvFileStorage
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public List<string> SavedPaths { get; } = [];

        public async Task<string> SaveAsync(Guid cvId, string extension, Stream content, DateTime uploadedAtUtc, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) == participants) _all.TrySetResult();
            await _all.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var path = await inner.SaveAsync(cvId, extension, content, uploadedAtUtc, cancellationToken);
            lock (SavedPaths) SavedPaths.Add(path);
            return path;
        }

        public Stream OpenRead(string storedPath) => inner.OpenRead(storedPath);
        public bool Exists(string storedPath) => inner.Exists(storedPath);
        public void Delete(string storedPath) => inner.Delete(storedPath);
    }

    private sealed class NoopQueue : ICvExtractionQueue
    {
        public ValueTask EnqueueAsync(Guid cvId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<Guid> DequeueAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

public sealed class A42SqlFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A42_TEST_SQL_CONNECTION";
    public const string Prefix = "SmartCv_A42_Test_";

    public A42SqlFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(Setting);
        if (string.IsNullOrWhiteSpace(value))
        {
            Skip = $"NOT RUN: set {Setting} for a new retained synthetic {Prefix} database.";
            return;
        }
        try { Validate(new SqlConnectionStringBuilder(value)); }
        catch (ArgumentException) { Skip = $"NOT RUN: {Setting} must designate {Prefix} without AttachDBFilename."; }
    }

    internal static void Validate(SqlConnectionStringBuilder value)
    {
        if (!value.InitialCatalog.StartsWith(Prefix, StringComparison.Ordinal) || !string.IsNullOrEmpty(value.AttachDBFilename))
            throw new ArgumentException($"Only {Prefix} without attached files is allowed.");
    }

    /// <summary>Catalog mới tạo bằng migration thật; giữ lại, không DROP.</summary>
    internal static async Task<string> NewDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Setting));
        Validate(builder);
        builder.InitialCatalog = Prefix + Guid.NewGuid().ToString("N");
        builder.Pooling = false;
        builder.ConnectTimeout = 10;
        var retained = TestSupport.RepoFile("artifacts/a42-tests/retained-sql-databases.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        await File.AppendAllTextAsync(retained, builder.InitialCatalog + Environment.NewLine);
        await using (var db = Db(builder.ConnectionString))
            await db.Database.MigrateAsync();
        return builder.ConnectionString;
    }

    internal static SmartCvDbContext Db(string connection) =>
        new(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options);

    internal static WebApplicationFactory<Program> Api(string connection) =>
        TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false" }).WithWebHostBuilder(b =>
        {
            b.ConfigureLogging(l => l.ClearProviders());
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                s.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(connection));
                foreach (var d in s.Where(d => d.ImplementationType == typeof(RunScoringWorker) || d.ImplementationType == typeof(RetentionWorker)).ToList())
                    s.Remove(d);
            });
        });
}
