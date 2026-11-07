using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Core.Uploads;

namespace SmartCv.Tests;

/// <summary>A5: CvFileValidator – kiểm tra rỗng, dung lượng, đuôi file (CheckBasic) và chữ ký đầu file (CheckSignature).</summary>
public class CvFileValidatorTests
{
    private static readonly CvUploadOptions Options = new();   // 10 MiB, .pdf .png .jpg .jpeg
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00];
    private static readonly byte[] PdfHeader = "%PDF-1.7\n"u8.ToArray();

    // ---------- CheckBasic ----------

    [Fact]
    public void Empty_file_is_rejected_first()
    {
        // Rỗng được kiểm tra TRƯỚC đuôi file: file .docx rỗng vẫn báo EmptyFile
        var check = CvFileValidator.CheckBasic("cv.docx", 0, Options);

        Assert.False(check.IsValid);
        Assert.Equal(CvUploadErrorCodes.EmptyFile, check.ErrorCode);
        Assert.Equal("File rỗng (0 byte).", check.ErrorMessage);
    }

    [Fact]
    public void Too_large_is_checked_before_extension()
    {
        var check = CvFileValidator.CheckBasic("cv.docx", 12L * 1024 * 1024, Options);

        Assert.Equal(CvUploadErrorCodes.TooLarge, check.ErrorCode);
        Assert.Equal("File nặng 12,0 MB, vượt giới hạn 10 MB.", check.ErrorMessage);
    }

    [Fact]
    public void Exactly_at_size_limit_is_allowed()
    {
        Assert.True(CvFileValidator.CheckBasic("cv.pdf", Options.MaxFileSizeBytes, Options).IsValid);
        Assert.Equal(CvUploadErrorCodes.TooLarge, CvFileValidator.CheckBasic("cv.pdf", Options.MaxFileSizeBytes + 1, Options).ErrorCode);
    }

    [Fact]
    public void Size_limit_comes_from_options()
    {
        var small = new CvUploadOptions { MaxFileSizeMB = 1 };

        Assert.Equal(CvUploadErrorCodes.TooLarge, CvFileValidator.CheckBasic("cv.pdf", 2L * 1024 * 1024, small).ErrorCode);
        Assert.True(CvFileValidator.CheckBasic("cv.pdf", 2L * 1024 * 1024, Options).IsValid);
    }

    [Theory]
    [InlineData("cv.pdf", ".pdf", "application/pdf")]
    [InlineData("CV.PDF", ".pdf", "application/pdf")]
    [InlineData("anh.png", ".png", "image/png")]
    [InlineData("anh.JPG", ".jpg", "image/jpeg")]
    [InlineData("anh.Jpeg", ".jpeg", "image/jpeg")]
    public void Allowed_extensions_are_case_insensitive(string name, string extension, string contentType)
    {
        var check = CvFileValidator.CheckBasic(name, 100, Options);

        Assert.True(check.IsValid);
        Assert.Equal(extension, check.Extension);
        Assert.Equal(contentType, check.ContentType);
    }

    [Theory]
    [InlineData("cv-word.docx", "Định dạng \".docx\" không được hỗ trợ. Chỉ nhận PDF, PNG, JPG.")]
    [InlineData("cv.exe", "Định dạng \".exe\" không được hỗ trợ. Chỉ nhận PDF, PNG, JPG.")]
    [InlineData("cv-khong-duoi", "File không có đuôi. Chỉ nhận PDF, PNG, JPG.")]
    public void Unsupported_or_missing_extension_is_invalid_format(string name, string message)
    {
        var check = CvFileValidator.CheckBasic(name, 100, Options);

        Assert.Equal(CvUploadErrorCodes.InvalidFormat, check.ErrorCode);
        Assert.Equal(message, check.ErrorMessage);
    }

    // ---------- Magic bytes ----------

    [Fact]
    public void Detects_pdf_png_jpeg_signatures()
    {
        Assert.Equal("application/pdf", CvFileValidator.DetectContentType(PdfHeader));
        Assert.Equal("image/png", CvFileValidator.DetectContentType(PngHeader));
        Assert.Equal("image/jpeg", CvFileValidator.DetectContentType(JpegHeader));
        Assert.Null(CvFileValidator.DetectContentType("Day khong phai CV"u8));
        Assert.Null(CvFileValidator.DetectContentType([]));
    }

    [Fact]
    public void Pdf_signature_may_appear_within_first_1024_bytes_only()
    {
        byte[] within = [.. new byte[1000], .. "%PDF-1.4"u8];
        byte[] beyond = [.. new byte[1024], .. "%PDF-1.4"u8];

        Assert.Equal("application/pdf", CvFileValidator.DetectContentType(within));
        Assert.Null(CvFileValidator.DetectContentType(beyond));
    }

    [Fact]
    public void Matching_content_passes_and_returns_detected_type()
    {
        var check = CvFileValidator.CheckSignature(".jpeg", JpegHeader);

        Assert.True(check.IsValid);
        Assert.Equal("image/jpeg", check.ContentType);
    }

    [Fact]
    public void Text_renamed_to_pdf_is_invalid()
    {
        var check = CvFileValidator.CheckSignature(".pdf", "Day khong phai CV"u8);

        Assert.Equal(CvUploadErrorCodes.InvalidFormat, check.ErrorCode);
        Assert.Equal("Nội dung không phải file PDF hợp lệ (có thể bị đổi đuôi hoặc bị hỏng).", check.ErrorMessage);
    }

    [Fact]
    public void Png_renamed_to_pdf_is_invalid_with_real_type_in_message()
    {
        var check = CvFileValidator.CheckSignature(".pdf", PngHeader);

        Assert.Equal(CvUploadErrorCodes.InvalidFormat, check.ErrorCode);
        Assert.Equal("Đuôi file là \".pdf\" nhưng nội dung là ảnh PNG. Hãy đổi lại đúng đuôi rồi tải lên.", check.ErrorMessage);
    }

    [Fact]
    public void Pdf_renamed_to_jpg_is_invalid()
    {
        var check = CvFileValidator.CheckSignature(".jpg", PdfHeader);

        Assert.Equal(CvUploadErrorCodes.InvalidFormat, check.ErrorCode);
        Assert.Contains("nội dung là PDF", check.ErrorMessage);
    }
}
