using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Extraction;

namespace SmartCv.Tests;

/// <summary>
/// A42 với OCR THẬT (opt-in): hai ảnh CV giả của gói A36_A42_B38_B42 (cv-anh-ro.png, cv-anh-mo.png) được đọc thẳng từ ZIP,
/// tải lên qua API, worker đọc chữ chạy Tesseract (eng+vie), rồi kiểm tra GET /api/cvs/{id}/text.
/// Không chép ảnh vào repo, không gọi Gemini; chỉ ghi các con số tổng hợp (không ghi chữ đã đọc) vào artifacts/a42-tests/ocr-evidence.txt.
/// Kết quả OCR phụ thuộc phiên bản Tesseract / dữ liệu tessdata của máy chạy.
/// </summary>
public class A42OcrDemoTests
{
    private const string Folder = "A36_B42_SmartCv/test-files/";

    [A42OcrDemoFact]
    public async Task Clear_image_reads_good_blurry_image_reads_low_and_reupload_points_to_existing_cv()
    {
        using var zip = ZipFile.OpenRead(Environment.GetEnvironmentVariable(A42OcrDemoFactAttribute.Setting)!);
        byte[] Read(string name)
        {
            using var stream = zip.GetEntry(Folder + name)!.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        var clear = Read("cv-anh-ro.png");
        var blurry = Read("cv-anh-mo.png");

        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" }, extractionWorker: true);
        using var client = factory.CreateClient();
        var ids = await UploadAsync(client, ("cv-anh-ro.png", clear), ("cv-anh-mo.png", blurry));
        var clearText = await WaitForTextAsync(client, ids[0]);
        var blurryText = await WaitForTextAsync(client, ids[1]);

        var evidence = TestSupport.RepoFile("artifacts/a42-tests/ocr-evidence.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await File.AppendAllLinesAsync(evidence,
        [
            $"{DateTime.UtcNow:O} Tesseract eng+vie, tessdata {TestSupport.TessdataPath}",
            Line("cv-anh-ro.png", clearText),
            Line("cv-anh-mo.png", blurryText)
        ]);

        Assert.Equal((ExtractionStatuses.Completed, ExtractionMethods.ImageOcr), (clearText.ExtractionStatus, clearText.ExtractionMethod));
        Assert.Equal((ExtractionStatuses.Completed, ExtractionMethods.ImageOcr), (blurryText.ExtractionStatus, blurryText.ExtractionMethod));
        Assert.Equal(TextQualityLevels.Good, clearText.Quality!.Level);
        Assert.Equal(TextQualityLevels.Low, blurryText.Quality!.Level);
        Assert.True(blurryText.Quality.MaybeMisread);
        Assert.Contains(TextQuality.OcrAdvice, blurryText.Quality.Warnings);

        // Tải lại ảnh rõ: trùng CV đã có → trả mã CV cũ để giao diện mở "Xem CV hiện có"
        using var again = new MultipartFormDataContent { { new ByteArrayContent(clear), "files", "cv-anh-ro-lan-2.png" } };
        using var response = await client.PostAsync("/api/cvs/upload", A38TestData.WithConsent(again));
        var dup = (await response.Content.ReadFromJsonAsync<CvUploadResponse>())!.Files.Single();
        Assert.Equal(CvUploadErrorCodes.Duplicate, dup.ErrorCode);
        Assert.Equal(ids[0], dup.ExistingCvId);
        Assert.Equal("cv-anh-ro.png", dup.ExistingFileName);
    }

    private static string Line(string name, CvTextDto t) =>
        $"  {name}: status={t.ExtractionStatus} method={t.ExtractionMethod} charCount={t.Quality?.CharCount} wordCount={t.Quality?.WordCount} " +
        $"letterRatio={t.Quality?.LetterRatio} level={t.Quality?.Level} maybeMisread={t.Quality?.MaybeMisread} warnings={t.Quality?.Warnings.Count}";

    private static async Task<Guid[]> UploadAsync(HttpClient client, params (string Name, byte[] Bytes)[] files)
    {
        using var content = new MultipartFormDataContent();
        foreach (var (name, bytes) in files) content.Add(new ByteArrayContent(bytes), "files", name);
        using var response = await client.PostAsync("/api/cvs/upload", A38TestData.WithConsent(content));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<CvUploadResponse>())!;
        Assert.All(body.Files, f => Assert.Equal(CvUploadStatuses.Accepted, f.Status));
        return body.Files.Select(f => f.CvId!.Value).ToArray();
    }

    /// <summary>Đợi worker đọc xong (tối đa 2 phút), rồi lấy GET /text.</summary>
    private static async Task<CvTextDto> WaitForTextAsync(HttpClient client, Guid id)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            var dto = (await client.GetFromJsonAsync<CvTextDto>($"/api/cvs/{id}/text"))!;
            if (dto.ExtractionStatus != ExtractionStatuses.Pending) return dto;
            Assert.True(DateTime.UtcNow < deadline, "OCR chưa xong sau 2 phút");
            await Task.Delay(500);
        }
    }
}

/// <summary>Chạy khi có tessdata (eng, vie) và biến SMARTCV_A42_DEMO_ZIP trỏ tới ZIP A36_A42_B38_B42; nếu không: Skipped (không tính là PASS).</summary>
public sealed class A42OcrDemoFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A42_DEMO_ZIP";

    public A42OcrDemoFactAttribute()
    {
        if (!TestSupport.HasTessdata) Skip = "NOT RUN: chưa có tessdata (chạy scripts/tai-tessdata.ps1).";
        else if (!File.Exists(Environment.GetEnvironmentVariable(Setting))) Skip = $"NOT RUN: đặt {Setting} trỏ tới ZIP A36_A42_B38_B42.";
    }
}
