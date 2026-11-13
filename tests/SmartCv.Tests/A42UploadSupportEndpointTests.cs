using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Extraction;
using SmartCv.Core.Privacy;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>
/// A42 qua pipeline HTTP (TestServer, InMemory, thư mục tạm; worker đọc chữ TẮT nên trạng thái đọc chữ do test đặt):
/// GET /api/cvs/{id}/text, textQuality trong GET /api/cvs/{id}, thông tin CV trùng khi tải lên, GET /api/runs/estimate.
/// Chữ CV là dữ liệu tổng hợp; không gọi OCR, không gọi Gemini.
/// </summary>
public class A42UploadSupportEndpointTests
{
    private static readonly DateTime Day = new(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc);

    private static readonly string ClearText = string.Join("\n", Enumerable.Repeat(
        "Kinh nghiệm: lập trình viên C# và ASP.NET Core tại công ty phần mềm, xây dựng API bán hàng với SQL Server.", 10));

    private static readonly string BlurryText = string.Join(" ", Enumerable.Repeat("K1nh ngh!3m: 2O23 | C# , A S P . N E T ~~ %% ## l a p tr1nh", 4));

    private sealed record Env(WebApplicationFactory<Program> Factory, HttpClient Client) : IDisposable
    {
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }

        public async Task<T> DbAsync<T>(Func<SmartCvDbContext, Task<T>> action)
        {
            using var scope = Factory.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
        }

        /// <summary>Thêm CV trực tiếp vào database (không qua upload) với trạng thái đọc chữ do test chọn.</summary>
        public Task<Guid> AddCvAsync(string status, string? text, string? method = ExtractionMethods.PdfText, string name = "CV_TongHop.pdf",
            string? hash = null, bool anonymized = false, string? error = null, params int[] jobIds) => DbAsync(async db =>
        {
            var id = Guid.NewGuid();
            db.Cvs.Add(new Cv
            {
                CvId = id, OriginalFileName = name, StoredPath = $"Uploads/CV/2026/11/{id}.pdf", ContentType = "application/pdf", FileSizeBytes = 100,
                FileHash = hash ?? id.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = status, ExtractedText = text,
                ExtractionMethod = method, ExtractionError = error, UploadedAt = Day, AnonymizedAt = anonymized ? Day.AddDays(30) : null,
                SourceChannel = CvSourceChannels.Referral, ConsentConfirmedAt = Day,
                Applications = jobIds.Select(j => new Application { JobId = j, CreatedAt = Day }).ToList()
            });
            await db.SaveChangesAsync();
            return id;
        });

        public async Task<CvUploadResponse> UploadAsync(params (string Name, byte[] Bytes)[] files)
        {
            using var content = new MultipartFormDataContent();
            foreach (var (name, bytes) in files) content.Add(new ByteArrayContent(bytes), "files", name);
            using var response = await Client.PostAsync("/api/cvs/upload", A38TestData.WithConsent(content));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<CvUploadResponse>())!;
        }
    }

    private static async Task<Env> CreateAsync(Dictionary<string, string?>? settings = null, string environment = "Development")
    {
        var factory = TestSupport.ApiFactory(environment, settings ?? new() { ["Scoring:WorkerEnabled"] = "false" })
            .WithWebHostBuilder(b => b.ConfigureLogging(l => l.ClearProviders()));
        var env = new Env(factory, factory.CreateClient());
        await env.DbAsync(db => db.Database.EnsureCreatedAsync());
        return env;
    }

    private static async Task<string> HashAsync(byte[] bytes) => await FileHasher.ComputeSha256Async(new MemoryStream(bytes));

    // =====================================================================================
    // GET /api/cvs/{id}/text
    // =====================================================================================

    [Fact]
    public async Task Text_of_completed_cv_has_quality_and_no_internal_path()
    {
        using var env = await CreateAsync();
        var id = await env.AddCvAsync(ExtractionStatuses.Completed, ClearText);

        using var response = await env.Client.GetAsync($"/api/cvs/{id}/text");
        var json = await response.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<CvTextDto>(json, JsonSerializerOptions.Web)!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("Uploads", json);
        Assert.DoesNotContain("storedPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((id, "CV_TongHop.pdf", ExtractionStatuses.Completed, ExtractionMethods.PdfText),
            (dto.CvId, dto.OriginalFileName, dto.ExtractionStatus, dto.ExtractionMethod));
        Assert.Equal(ClearText, dto.Text);
        Assert.Equal("Đã đọc xong.", dto.StatusMessage);
        Assert.Equal(TextQualityLevels.Good, dto.Quality!.Level);
        Assert.False(dto.Quality.MaybeMisread);
        Assert.Equal(ClearText.Length, dto.Quality.CharCount);
        Assert.InRange(dto.Quality.LetterRatio, 0.65m, 1m);   // thang 0–1, không phải %
        foreach (var field in new[] { "cvId", "originalFileName", "extractionStatus", "extractionMethod", "extractionError", "text", "quality", "statusMessage" })
            Assert.Contains($"\"{field}\"", json);
        foreach (var field in new[] { "charCount", "wordCount", "letterRatio", "level", "maybeMisread", "warnings" })
            Assert.Contains($"\"{field}\"", json);
    }

    [Fact]
    public async Task Text_of_blurry_ocr_cv_is_low_with_ocr_advice()
    {
        using var env = await CreateAsync();
        var id = await env.AddCvAsync(ExtractionStatuses.Completed, BlurryText, ExtractionMethods.ImageOcr, "cv-anh-mo.png");

        var dto = (await env.Client.GetFromJsonAsync<CvTextDto>($"/api/cvs/{id}/text"))!;

        Assert.Equal(TextQualityLevels.Low, dto.Quality!.Level);
        Assert.True(dto.Quality.MaybeMisread);
        Assert.Contains(TextQuality.OcrAdvice, dto.Quality.Warnings);
        Assert.True(dto.Quality.LetterRatio < 0.65m);
    }

    [Fact]
    public async Task Text_of_completed_cv_without_any_text_is_empty()
    {
        using var env = await CreateAsync();
        var id = await env.AddCvAsync(ExtractionStatuses.Completed, "  ", ExtractionMethods.ImageOcr);

        var dto = (await env.Client.GetFromJsonAsync<CvTextDto>($"/api/cvs/{id}/text"))!;

        Assert.Equal(TextQualityLevels.Empty, dto.Quality!.Level);
        Assert.Contains("không có chữ", dto.StatusMessage);
    }

    [Theory]
    [InlineData(ExtractionStatuses.Pending, "Chữ cũ của lần đọc trước", null, "đang chờ đọc chữ")]
    [InlineData(ExtractionStatuses.Failed, null, "Không đọc được file PDF.", "Không đọc được chữ")]
    public async Task Pending_and_failed_report_status_honestly_without_text_or_quality(string status, string? text, string? error, string message)
    {
        using var env = await CreateAsync();
        var id = await env.AddCvAsync(status, text, error: error);

        using var response = await env.Client.GetAsync($"/api/cvs/{id}/text");
        var json = await response.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<CvTextDto>(json, JsonSerializerOptions.Web)!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(status, dto.ExtractionStatus);
        Assert.Null(dto.Text);
        Assert.Null(dto.Quality);
        Assert.Contains(message, dto.StatusMessage);
        Assert.Equal(error, dto.ExtractionError);
        Assert.DoesNotContain("Chữ cũ", json);
        Assert.DoesNotContain("Good", json);
    }

    [Fact]
    public async Task Text_of_unknown_cv_is_404_and_of_anonymized_cv_is_410_without_content()
    {
        using var env = await CreateAsync();
        // Dòng giả còn sót chữ và tên file để chứng minh 410 trả về TRƯỚC khi đụng tới nội dung
        var anonymized = await env.AddCvAsync(ExtractionStatuses.Completed, "Nội dung bí mật A42", name: "Bi-mat-A42.pdf", anonymized: true);

        using var unknown = await env.Client.GetAsync($"/api/cvs/{Guid.NewGuid()}/text");
        using var gone = await env.Client.GetAsync($"/api/cvs/{anonymized}/text");
        using var notGuid = await env.Client.GetAsync("/api/cvs/khong-phai-guid/text");
        var goneBody = await gone.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Không tìm thấy CV", JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Equal("CV đã được ẩn danh hóa", JsonDocument.Parse(goneBody).RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("bí mật", goneBody);
        Assert.DoesNotContain("Bi-mat", goneBody);
        Assert.Equal(HttpStatusCode.NotFound, notGuid.StatusCode);
    }

    // =====================================================================================
    // GET /api/cvs/{id}: textQuality, giữ nguyên trường A38/A39
    // =====================================================================================

    [Fact]
    public async Task Detail_adds_text_quality_and_keeps_every_a38_field()
    {
        using var env = await CreateAsync();
        var id = await env.AddCvAsync(ExtractionStatuses.Completed, ClearText, jobIds: [1]);

        using var response = await env.Client.GetAsync($"/api/cvs/{id}");
        var json = await response.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<CvDetailDto>(json, JsonSerializerOptions.Web)!;

        foreach (var field in new[] { "cvId", "candidateId", "candidateName", "originalFileName", "contentType", "fileSizeBytes", "uploadedAt",
                     "fileAvailable", "sourceChannel", "sourceChannelName", "consentConfirmedAt", "retainUntil", "anonymizedAt", "isAnonymized",
                     "extractionStatus", "extractionMethod", "extractionError", "extractedText", "applications", "textQuality" })
            Assert.Contains($"\"{field}\"", json);
        Assert.Equal(CvSourceChannels.Referral, dto.SourceChannel);
        Assert.Equal(Day, dto.ConsentConfirmedAt);
        Assert.Equal(ClearText, dto.ExtractedText);
        Assert.Single(dto.Applications);
        Assert.Equal(TextQualityLevels.Good, dto.TextQuality!.Level);
    }

    [Theory]
    [InlineData(ExtractionStatuses.Pending, "Chữ cũ của lần đọc trước", false)]
    [InlineData(ExtractionStatuses.Failed, null, false)]
    [InlineData(ExtractionStatuses.Completed, "Chữ còn sót", true)]
    public async Task Detail_has_no_text_quality_when_pending_failed_or_anonymized(string status, string? text, bool anonymized)
    {
        using var env = await CreateAsync();
        var id = await env.AddCvAsync(status, text, anonymized: anonymized);

        var dto = (await env.Client.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{id}"))!;

        Assert.Null(dto.TextQuality);
        if (anonymized)
        {
            Assert.Null(dto.ExtractedText);
            Assert.Equal(RetentionRules.AnonymizedFileName, dto.OriginalFileName);
        }
    }

    // =====================================================================================
    // POST /api/cvs/upload: thông tin CV trùng
    // =====================================================================================

    [Fact]
    public async Task Duplicate_of_saved_cv_returns_existing_cv_jobs_and_keeps_old_consent()
    {
        using var env = await CreateAsync();
        var bytes = TestSupport.Pdf("a42-da-co");
        var old = await env.AddCvAsync(ExtractionStatuses.Completed, ClearText, name: "CV_DaCo.pdf", hash: await HashAsync(bytes), jobIds: [2, 1]);
        var jobs = await env.DbAsync(db => db.Jobs.Where(j => j.JobId == 1 || j.JobId == 2).OrderBy(j => j.JobId).Select(j => j.Title).ToListAsync());

        var result = await env.UploadAsync(("cv-moi.pdf", bytes));

        var dup = Assert.Single(result.Files);
        Assert.Equal((CvUploadStatuses.Rejected, CvUploadErrorCodes.Duplicate), (dup.Status, dup.ErrorCode));
        Assert.Null(dup.CvId);
        Assert.Equal(old, dup.ExistingCvId);
        Assert.Equal("CV_DaCo.pdf", dup.ExistingFileName);
        Assert.Equal(Day, dup.ExistingUploadedAt);
        Assert.Equal(DateTimeKind.Utc, dup.ExistingUploadedAt!.Value.Kind);
        Assert.Equal(jobs, dup.ExistingJobTitles);   // theo JobId, không trùng lặp
        Assert.StartsWith("Trùng với CV đã tải trước đó: \"CV_DaCo.pdf\" (ngày ", dup.ErrorMessage);   // tiền tố A5 giữ nguyên
        Assert.EndsWith($", đã ứng tuyển: {string.Join(", ", jobs)}.", dup.ErrorMessage);
        Assert.DoesNotContain("Uploads", dup.ErrorMessage);
        Assert.Equal((0, 1), (result.SuccessCount, result.FailedCount));

        var cv = await env.DbAsync(db => db.Cvs.AsNoTracking().SingleAsync(c => c.CvId == old));
        Assert.Equal((CvSourceChannels.Referral, (DateTime?)Day), (cv.SourceChannel, cv.ConsentConfirmedAt));   // không đổi nguồn / đồng ý cũ
        Assert.Equal(1, await env.DbAsync(db => db.Cvs.CountAsync()));
    }

    [Fact]
    public async Task Duplicate_of_cv_without_applications_says_so()
    {
        using var env = await CreateAsync();
        var bytes = TestSupport.Pdf("a42-chua-ung-tuyen");
        var old = await env.AddCvAsync(ExtractionStatuses.Pending, null, hash: await HashAsync(bytes));

        var dup = (await env.UploadAsync(("cv.pdf", bytes))).Files[0];

        Assert.Equal(old, dup.ExistingCvId);
        Assert.Empty(dup.ExistingJobTitles);
        Assert.EndsWith(", chưa ứng tuyển vị trí nào.", dup.ErrorMessage);
    }

    [Fact]
    public async Task Mixed_batch_keeps_order_counts_and_never_gives_old_cv_id_to_in_batch_duplicates()
    {
        using var env = await CreateAsync();
        var saved = TestSupport.Pdf("a42-da-luu");
        var old = await env.AddCvAsync(ExtractionStatuses.Completed, ClearText, hash: await HashAsync(saved), jobIds: [1]);
        var fresh = TestSupport.Pdf("a42-moi");

        var result = await env.UploadAsync(
            ("moi.pdf", fresh),
            ("moi-ban-sao.pdf", fresh),                       // trùng file đầu trong cùng lần tải
            ("da-luu.pdf", saved),                            // trùng CV trong database
            ("gia.pdf", "khong phai pdf"u8.ToArray()),        // sai định dạng
            ("khac.pdf", TestSupport.Pdf("a42-khac")));

        Assert.Equal([CvUploadStatuses.Accepted, CvUploadStatuses.Rejected, CvUploadStatuses.Rejected, CvUploadStatuses.Rejected, CvUploadStatuses.Accepted],
            result.Files.Select(f => f.Status));
        Assert.Equal(["moi.pdf", "moi-ban-sao.pdf", "da-luu.pdf", "gia.pdf", "khac.pdf"], result.Files.Select(f => f.FileName));
        Assert.Equal((5, 2, 3), (result.TotalFiles, result.SuccessCount, result.FailedCount));

        var inBatch = result.Files[1];
        Assert.Equal(CvUploadErrorCodes.Duplicate, inBatch.ErrorCode);
        Assert.Equal("Trùng nội dung với file \"moi.pdf\" trong cùng lần tải.", inBatch.ErrorMessage);
        Assert.Null(inBatch.ExistingCvId);
        Assert.Null(inBatch.ExistingFileName);
        Assert.Null(inBatch.ExistingUploadedAt);
        Assert.Empty(inBatch.ExistingJobTitles);

        Assert.Equal(old, result.Files[2].ExistingCvId);
        Assert.Equal(CvUploadErrorCodes.InvalidFormat, result.Files[3].ErrorCode);
        Assert.Null(result.Files[3].ExistingCvId);
        Assert.All(new[] { result.Files[0], result.Files[4] }, f => Assert.Null(f.ExistingCvId));
        Assert.Equal(3, await env.DbAsync(db => db.Cvs.CountAsync()));   // CV cũ + 2 CV mới
    }

    [Fact]
    public async Task Duplicate_of_anonymized_cv_reveals_nothing_about_the_old_cv()
    {
        using var env = await CreateAsync();
        var bytes = TestSupport.Pdf("a42-an-danh");
        // A39 đổi FileHash khi ẩn danh hóa nên thực tế không khớp; dòng giả giữ hash cũ để chứng minh nhánh phòng thủ
        var old = await env.AddCvAsync(ExtractionStatuses.Completed, null, name: "Bi-mat-cu.pdf", hash: await HashAsync(bytes), anonymized: true, jobIds: [1]);

        using var content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "files", "cv.pdf" } };
        using var response = await env.Client.PostAsync("/api/cvs/upload", A38TestData.WithConsent(content));
        var json = await response.Content.ReadAsStringAsync();
        var dup = JsonSerializer.Deserialize<CvUploadResponse>(json, JsonSerializerOptions.Web)!.Files[0];

        Assert.Equal(CvUploadErrorCodes.Duplicate, dup.ErrorCode);
        Assert.Null(dup.ExistingCvId);
        Assert.Null(dup.ExistingFileName);
        Assert.Null(dup.ExistingUploadedAt);
        Assert.Empty(dup.ExistingJobTitles);
        Assert.Contains("ẩn danh hóa", dup.ErrorMessage);
        Assert.DoesNotContain("Bi-mat", json);
        Assert.DoesNotContain(old.ToString(), json);
    }

    [Fact]
    public async Task Upload_without_consent_is_400_saves_nothing_and_leaks_no_duplicate_info()
    {
        using var env = await CreateAsync();
        var bytes = TestSupport.Pdf("a42-thieu-dong-y");
        var old = await env.AddCvAsync(ExtractionStatuses.Completed, ClearText, name: "CV_DaCo.pdf", hash: await HashAsync(bytes));

        using var content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "files", "cv.pdf" }, { new ByteArrayContent(TestSupport.Pdf("x")), "files", "x.pdf" } };
        content.Add(new StringContent("Email"), "sourceChannel");
        using var response = await env.Client.PostAsync("/api/cvs/upload", content);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(old.ToString(), json);
        Assert.DoesNotContain("CV_DaCo", json);
        Assert.Equal(1, await env.DbAsync(db => db.Cvs.CountAsync()));
    }

    // =====================================================================================
    // GET /api/runs/estimate?count=N
    // =====================================================================================

    private static EvaluationRun Run(int prompt, string type, string status, int items, DateTime created, double? seconds, int? setId = null) => new()
    {
        RunType = type, PromptVersionId = prompt, EvaluationSetId = setId, ModelName = "gemini-test", Temperature = 0.2m,
        KeywordWeight = 0.3m, AiWeight = 0.7m, PassThreshold = 70m, Status = status, TotalItems = items, ProcessedItems = items,
        CreatedAt = created, CompletedAt = seconds is null ? null : created.AddSeconds(seconds.Value)
    };

    private static Task<int> SeedRunsAsync(Env env, Func<int, int, IEnumerable<EvaluationRun>> runs) => env.DbAsync(async db =>
    {
        var prompt = new PromptVersion { VersionName = "v-a42", SystemPrompt = "Prompt A42", IsActive = true, CreatedAt = Day };
        var set = new EvaluationSet { Name = "Bộ A42", CreatedAt = Day };
        db.AddRange(prompt, set);
        await db.SaveChangesAsync();
        db.EvaluationRuns.AddRange(runs(prompt.PromptVersionId, set.EvaluationSetId));
        await db.SaveChangesAsync();
        return await db.EvaluationRuns.MaxAsync(r => r.RunId);
    });

    [Fact]
    public async Task Estimate_without_history_uses_gemini_default()
    {
        using var env = await CreateAsync();

        using var response = await env.Client.GetAsync("/api/runs/estimate?count=10");
        var dto = (await response.Content.ReadFromJsonAsync<RunEstimateDto>())!;   // cùng cách đọc của client Blazor (ApiClientBase)

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((10, 6.5m, 65, 2, 0), (dto.Count, dto.SecondsPerCv, dto.EstimatedSeconds, dto.EstimatedMinutes, dto.BasedOnRuns));
        Assert.True(dto.IsDefault);
        Assert.False(dto.AiSimulated);
        Assert.Equal(4.5m, dto.MinSecondsPerCv);
        Assert.Equal("10 CV · khoảng 2 phút", dto.Text);
        Assert.StartsWith("Ước tính ban đầu", dto.Note);
        Assert.Contains("không phải cam kết", dto.Note);
    }

    [Theory]
    [InlineData("800", 2.8)]
    [InlineData("0", 2.0)]
    public async Task Estimate_with_fake_ai_uses_fake_delay_and_says_so(string fakeDelayMs, double perCv)
    {
        using var env = await CreateAsync(new()
        {
            ["Scoring:WorkerEnabled"] = "false", ["Gemini:UseFake"] = "true", ["Gemini:FakeDelayMs"] = fakeDelayMs
        });

        var dto = (await env.Client.GetFromJsonAsync<RunEstimateDto>("/api/runs/estimate?count=10"))!;

        Assert.Equal((decimal)perCv, dto.SecondsPerCv);
        Assert.True(dto.AiSimulated);
        Assert.Contains("AI giả lập", dto.Note);
    }

    [Fact]
    public async Task Estimate_uses_only_the_ten_latest_completed_screening_runs()
    {
        using var env = await CreateAsync();
        var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedRunsAsync(env, (prompt, set) =>
        [
            // 10 lượt mới nhất hợp lệ: 10 CV / 80 giây → 8 giây/CV
            .. Enumerable.Range(0, 10).Select(i => Run(prompt, RunTypes.Screening, RunStatuses.Completed, 10, t0.AddHours(10 + i), 80)),
            // cũ hơn (lượt thứ 11, 12): rất chậm, không được dùng
            Run(prompt, RunTypes.Screening, RunStatuses.Completed, 1, t0.AddHours(1), 1_000),
            Run(prompt, RunTypes.Screening, RunStatuses.Completed, 1, t0.AddHours(2), 1_000),
            // mới hơn nhưng không hợp lệ: PromptTest, đang chạy, lỗi, không có hồ sơ, thời gian 0
            Run(prompt, RunTypes.PromptTest, RunStatuses.Completed, 1, t0.AddHours(30), 5_000, set),
            Run(prompt, RunTypes.Screening, RunStatuses.Running, 5, t0.AddHours(31), null),
            Run(prompt, RunTypes.Screening, RunStatuses.Queued, 5, t0.AddHours(32), null),
            Run(prompt, RunTypes.Screening, RunStatuses.Failed, 5, t0.AddHours(33), 9_000),
            Run(prompt, RunTypes.Screening, RunStatuses.Completed, 0, t0.AddHours(34), 60),
            Run(prompt, RunTypes.Screening, RunStatuses.Completed, 5, t0.AddHours(35), 0)
        ]);

        var dto = (await env.Client.GetFromJsonAsync<RunEstimateDto>("/api/runs/estimate?count=10"))!;

        Assert.Equal((8m, 80, 2, 10), (dto.SecondsPerCv, dto.EstimatedSeconds, dto.EstimatedMinutes, dto.BasedOnRuns));
        Assert.False(dto.IsDefault);
        Assert.Equal("10 CV · khoảng 2 phút", dto.Text);
        Assert.StartsWith("Ước tính theo 10 lượt chấm gần nhất", dto.Note);
    }

    [Theory]
    [InlineData("count=-1")]
    [InlineData("count=501")]
    [InlineData("count=")]
    [InlineData("count=abc")]
    [InlineData("count=1.5")]
    [InlineData("count=99999999999")]
    [InlineData("")]
    public async Task Estimate_rejects_invalid_count_with_vietnamese_400(string query)
    {
        using var env = await CreateAsync();

        using var response = await env.Client.GetAsync($"/api/runs/estimate?{query}");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Số CV không hợp lệ", JsonDocument.Parse(text).RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("Exception", text);
    }

    [Theory]
    [InlineData(0, "0 CV")]
    [InlineData(500, "500 CV · khoảng 55 phút")]
    public async Task Estimate_accepts_the_boundaries(int count, string text)
    {
        using var env = await CreateAsync();

        var dto = (await env.Client.GetFromJsonAsync<RunEstimateDto>($"/api/runs/estimate?count={count}"))!;

        Assert.Equal(count, dto.Count);
        Assert.Equal(text, dto.Text);
    }

    [Fact]
    public async Task Estimate_route_does_not_shadow_run_routes()
    {
        using var env = await CreateAsync();
        var runId = await SeedRunsAsync(env, (prompt, _) => [Run(prompt, RunTypes.Screening, RunStatuses.Completed, 2, Day, 30)]);

        using var byId = await env.Client.GetAsync($"/api/runs/{runId}");
        using var missing = await env.Client.GetAsync("/api/runs/999999");
        using var estimate = await env.Client.GetAsync("/api/runs/estimate?count=1");

        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        Assert.Equal(runId, JsonDocument.Parse(await byId.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, estimate.StatusCode);
    }

    // =====================================================================================
    // Bảo vệ: chỉ Development + CvUpload:Endpoint:Enabled, giữ giới hạn tải lên
    // =====================================================================================

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task A42_endpoints_do_not_exist_outside_development(string environment)
    {
        using var factory = TestSupport.ApiFactory(environment);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cvs/{Guid.NewGuid()}/text")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/estimate?count=10")).StatusCode);
    }

    [Fact]
    public async Task A42_endpoints_do_not_exist_when_development_endpoints_are_disabled()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["CvUpload:Endpoint:Enabled"] = "false" });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cvs/{Guid.NewGuid()}/text")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/estimate?count=10")).StatusCode);
    }

    [Fact]
    public void Upload_keeps_rate_limit_and_size_limit_and_new_read_endpoints_add_none()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        RouteEndpoint Find(string pattern) => endpoints.Single(e => e.RoutePattern.RawText == pattern);

        var upload = Find("/api/cvs/upload");
        Assert.Equal("cv-upload", upload.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.NotNull(upload.Metadata.GetMetadata<IRequestSizeLimitMetadata>());
        foreach (var pattern in new[] { "/api/cvs/{id:guid}/text", "/api/runs/estimate" })
        {
            var endpoint = Find(pattern);
            Assert.Contains("GET", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
            Assert.Null(endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>());
        }
    }
}
