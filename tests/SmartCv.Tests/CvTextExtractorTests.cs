using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Extraction;

namespace SmartCv.Tests;

/// <summary>
/// A7: đọc chữ thật trên bộ CV mẫu test-files/cv-mau-a7 (thông tin giả).
/// Test có OCR cần tessdata (scripts/tai-tessdata.ps1); máy chưa tải thì các test đó báo Skipped.
/// </summary>
public class CvTextExtractorTests
{
    private static CvTextExtractor Create(string? tessdata = null, Action<OcrOptions>? configure = null)
    {
        var options = new OcrOptions { TessdataPath = tessdata ?? TestSupport.TessdataPath };
        configure?.Invoke(options);
        var wrapped = Options.Create(options);
        return new CvTextExtractor(
            new TesseractOcrEngine(wrapped, NullLogger<TesseractOcrEngine>.Instance), wrapped, NullLogger<CvTextExtractor>.Instance);
    }

    private static async Task<TextExtractionResult> ExtractAsync(CvTextExtractor extractor, string file, string contentType)
    {
        await using var stream = File.OpenRead(TestSupport.RepoFile(Path.Combine("test-files", "cv-mau-a7", file)));
        return await extractor.ExtractAsync(stream, contentType);
    }

    [Theory]
    [InlineData("cv-backend-pdf-chu.pdf")]
    [InlineData("cv-frontend-pdf-chu.pdf")]
    public async Task Text_pdf_is_read_with_pdfpig(string file)
    {
        // Không cần tessdata: PDF có lớp chữ không qua OCR
        var result = await ExtractAsync(Create(tessdata: TestSupport.TempRoot()), file, "application/pdf");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ExtractionMethods.PdfText, result.Method);
        Assert.Contains("example.com", result.Text);
        Assert.Contains("KINH NGHIỆM", result.Text);           // tiếng Việt có dấu, dạng NFC
        Assert.True(result.Text!.IsNormalized(System.Text.NormalizationForm.FormC));
    }

    [Fact]
    public async Task Password_protected_pdf_fails_with_clear_message()
    {
        var result = await ExtractAsync(Create(), "cv-co-mat-khau.pdf", "application/pdf");

        Assert.False(result.IsSuccess);
        Assert.Equal(ExtractionMethods.PdfText, result.Method);
        Assert.Equal("Không đọc được nội dung: file PDF có mật khẩu.", result.Error);
    }

    [Fact]
    public async Task Broken_pdf_fails_without_technical_details()
    {
        var result = await Create().ExtractAsync(new MemoryStream(TestSupport.Pdf("hong")), "application/pdf");

        Assert.False(result.IsSuccess);
        Assert.Equal("Không đọc được nội dung: file PDF bị hỏng hoặc có mật khẩu.", result.Error);
    }

    [OcrFact]
    public async Task Scanned_pdf_is_read_with_ocr()
    {
        var result = await ExtractAsync(Create(), "cv-ai-pdf-scan.pdf", "application/pdf");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ExtractionMethods.PdfOcr, result.Method);
        Assert.Contains("Python", result.Text);
        Assert.Contains("Machine Learning", result.Text);
    }

    [OcrFact]
    public async Task Clear_png_is_read_with_ocr()
    {
        var result = await ExtractAsync(Create(), "cv-tester-anh.png", "image/png");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ExtractionMethods.ImageOcr, result.Method);
        Assert.Contains("Postman", result.Text);
        Assert.Contains("Selenium", result.Text);
        Assert.Contains("Kiểm thử", result.Text);              // tiếng Việt có dấu (eng+vie)
    }

    [OcrFact]
    public async Task Photo_like_jpg_is_read_with_ocr()
    {
        var result = await ExtractAsync(Create(), "cv-ba-anh-chup.jpg", "image/jpeg");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ExtractionMethods.ImageOcr, result.Method);
        Assert.Contains("BPMN", result.Text);
        Assert.Contains("Figma", result.Text);
    }

    [Fact]
    public async Task Missing_tessdata_fails_with_fixed_message_without_server_path()
    {
        var emptyDir = TestSupport.TempRoot();

        var image = await ExtractAsync(Create(tessdata: emptyDir), "cv-tester-anh.png", "image/png");
        var scan = await ExtractAsync(Create(tessdata: emptyDir), "cv-ai-pdf-scan.pdf", "application/pdf");

        foreach (var r in new[] { image, scan })
        {
            Assert.False(r.IsSuccess);
            Assert.Equal(CvTextExtractor.MissingOcrDataError, r.Error);
            Assert.DoesNotContain(emptyDir, r.Error);
            Assert.DoesNotContain(":\\", r.Error);
        }
        Assert.Equal(ExtractionMethods.ImageOcr, image.Method);
        Assert.Equal(ExtractionMethods.PdfOcr, scan.Method);
    }

    [Fact]
    public async Task Image_over_pixel_limit_is_rejected_before_ocr()
    {
        // Ảnh mẫu khoảng 1240×1754 điểm ảnh > giới hạn 1 triệu: bị chặn trước khi giải nén / OCR
        var result = await ExtractAsync(Create(tessdata: TestSupport.TempRoot(), o => o.MaxImagePixels = 1_000_000),
            "cv-tester-anh.png", "image/png");

        Assert.False(result.IsSuccess);
        Assert.Contains("độ phân giải quá lớn", result.Error);
    }

    [Fact]
    public async Task Corrupted_image_fails_cleanly()
    {
        var result = await Create().ExtractAsync(new MemoryStream(TestSupport.Png("khong-phai-anh-that")), "image/png");

        Assert.False(result.IsSuccess);
        Assert.Equal(ExtractionMethods.ImageOcr, result.Method);
        Assert.Equal("Không đọc được nội dung: file ảnh bị hỏng.", result.Error);
    }
}
