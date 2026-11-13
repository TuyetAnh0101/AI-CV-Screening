using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>A7: POST /api/cvs/{id}/extract và luồng tải lên → đọc chữ nền qua pipeline HTTP.</summary>
public class CvExtractEndpointTests
{
    private static async Task<Guid> UploadAsync(HttpClient client, byte[] bytes, string name)
    {
        using var content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "files", name } };
        using var response = await client.PostAsync("/api/cvs/upload", A38TestData.WithConsent(content));
        var body = await response.Content.ReadFromJsonAsync<CvUploadResponse>();
        Assert.Equal(CvUploadStatuses.Accepted, body!.Files[0].Status);
        return body.Files[0].CvId!.Value;
    }

    private static async Task<CvDetailDto> WaitUntilDoneAsync(HttpClient client, Guid id, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var dto = await client.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{id}");
            if (dto!.ExtractionStatus != ExtractionStatuses.Pending || DateTime.UtcNow > deadline) return dto;
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task Reextract_resets_failed_cv_to_pending_and_returns_202()
    {
        var dbName = Guid.NewGuid().ToString("N");
        using var factory = TestSupport.ApiFactory(dbName: dbName);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            db.Cvs.Add(new Cv
            {
                CvId = id, OriginalFileName = "a.png", StoredPath = $"Uploads/CV/2026/11/{id}.png", ContentType = "image/png",
                FileSizeBytes = 1, FileHash = new string('B', 64), ExtractionStatus = ExtractionStatuses.Failed,
                ExtractionMethod = ExtractionMethods.ImageOcr, ExtractionError = "Thiếu dữ liệu OCR", UploadedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var response = await client.PostAsync($"/api/cvs/{id}/extract", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/api/cvs/{id}", response.Headers.Location?.OriginalString);
        var body = await response.Content.ReadFromJsonAsync<CvExtractionAcceptedDto>();
        Assert.Equal(id, body!.CvId);
        Assert.Equal(ExtractionStatuses.Pending, body.ExtractionStatus);

        var detail = await client.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{id}");
        Assert.Equal(ExtractionStatuses.Pending, detail!.ExtractionStatus);
        Assert.Null(detail.ExtractionError);
    }

    [Fact]
    public async Task Reextract_unknown_cv_returns_404()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync($"/api/cvs/{Guid.NewGuid()}/extract", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reextract_is_not_exposed_in_production()
    {
        using var factory = TestSupport.ApiFactory("Production");
        using var client = factory.CreateClient();

        using var response = await client.PostAsync($"/api/cvs/{Guid.NewGuid()}/extract", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Upload_then_background_worker_marks_broken_pdf_failed()
    {
        // Không cần tessdata: PDF hỏng bị PdfPig từ chối trước khi tới OCR
        using var factory = TestSupport.ApiFactory(extractionWorker: true);
        using var client = factory.CreateClient();

        var id = await UploadAsync(client, TestSupport.Pdf("hong-" + Guid.NewGuid()), "hong.pdf");
        var dto = await WaitUntilDoneAsync(client, id);

        Assert.Equal(ExtractionStatuses.Failed, dto.ExtractionStatus);
        Assert.Equal("Không đọc được nội dung: file PDF bị hỏng hoặc có mật khẩu.", dto.ExtractionError);
        Assert.Null(dto.ExtractedText);
    }

    [Fact]
    public async Task Upload_text_pdf_then_worker_completes_with_pdftext()
    {
        using var factory = TestSupport.ApiFactory(extractionWorker: true);
        using var client = factory.CreateClient();
        var bytes = await File.ReadAllBytesAsync(TestSupport.RepoFile(Path.Combine("test-files", "cv-mau-a7", "cv-backend-pdf-chu.pdf")));

        var id = await UploadAsync(client, bytes, "cv-backend-pdf-chu.pdf");
        var dto = await WaitUntilDoneAsync(client, id);

        Assert.Equal(ExtractionStatuses.Completed, dto.ExtractionStatus);
        Assert.Equal(ExtractionMethods.PdfText, dto.ExtractionMethod);
        Assert.Contains("example.com", dto.ExtractedText);
    }

    [OcrFact]
    public async Task Upload_image_then_worker_completes_with_ocr()
    {
        using var factory = TestSupport.ApiFactory(extractionWorker: true,
            settings: new() { ["TextExtraction:TessdataPath"] = TestSupport.TessdataPath });
        using var client = factory.CreateClient();
        var bytes = await File.ReadAllBytesAsync(TestSupport.RepoFile(Path.Combine("test-files", "cv-mau-a7", "cv-tester-anh.png")));

        var id = await UploadAsync(client, bytes, "cv-tester-anh.png");
        var dto = await WaitUntilDoneAsync(client, id);

        Assert.Equal(ExtractionStatuses.Completed, dto.ExtractionStatus);
        Assert.Equal(ExtractionMethods.ImageOcr, dto.ExtractionMethod);
        Assert.Contains("Postman", dto.ExtractedText);
    }
}
