using System.Globalization;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;

namespace SmartCv.Core.Uploads;

/// <summary>
/// A5: kiểm tra một file CV. Chỉ là logic thuần (không database, không HTTP), nên test được bằng unit test.
/// Thứ tự kiểm tra: rỗng → quá lớn → đuôi file → nội dung thật (chữ ký đầu file).
/// Kiểm tra trùng (hash) cần database nên nằm ở CvUploadService.
/// </summary>
public static class CvFileValidator
{
    /// <summary>Số byte đầu file cần đọc để nhận diện loại file.</summary>
    public const int HeaderLength = 1024;

    // Chữ ký (magic bytes) ở đầu mỗi loại file.
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // \x89PNG\r\n\x1a\n
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    /// <summary>Bước 1: kiểm tra những gì biết được mà chưa cần đọc nội dung (tên, dung lượng).</summary>
    public static CvFileCheck CheckBasic(string fileName, long length, CvUploadOptions options)
    {
        if (length <= 0)
            return CvFileCheck.Fail(CvUploadErrorCodes.EmptyFile, "File rỗng (0 byte).");

        if (length > options.MaxFileSizeBytes)
            return CvFileCheck.Fail(CvUploadErrorCodes.TooLarge,
                $"File nặng {ToMegabytes(length)} MB, vượt giới hạn {options.MaxFileSizeMB} MB.");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (string.IsNullOrEmpty(extension))
            return CvFileCheck.Fail(CvUploadErrorCodes.InvalidFormat,
                "File không có đuôi. Chỉ nhận PDF, PNG, JPG.");

        if (!options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return CvFileCheck.Fail(CvUploadErrorCodes.InvalidFormat,
                $"Định dạng \"{extension}\" không được hỗ trợ. Chỉ nhận PDF, PNG, JPG.");

        return CvFileCheck.Ok(extension, ContentTypeFor(extension));
    }

    /// <summary>
    /// Bước 2: đọc vài byte đầu để chắc nội dung đúng là PDF/PNG/JPG, không phải file khác bị đổi đuôi.
    /// Không tin đuôi file hay ContentType do trình duyệt gửi, vì người dùng đổi được dễ dàng.
    /// </summary>
    public static CvFileCheck CheckSignature(string extension, ReadOnlySpan<byte> header)
    {
        var detected = DetectContentType(header);
        var expected = ContentTypeFor(extension);

        if (detected is null)
            return CvFileCheck.Fail(CvUploadErrorCodes.InvalidFormat,
                $"Nội dung không phải file {FriendlyName(expected)} hợp lệ (có thể bị đổi đuôi hoặc bị hỏng).");

        if (detected != expected)
            return CvFileCheck.Fail(CvUploadErrorCodes.InvalidFormat,
                $"Đuôi file là \"{extension}\" nhưng nội dung là {FriendlyName(detected)}. Hãy đổi lại đúng đuôi rồi tải lên.");

        return CvFileCheck.Ok(extension, detected);
    }

    /// <summary>Nhận diện loại file từ các byte đầu. Trả null nếu không phải PDF/PNG/JPG.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(PngSignature)) return "image/png";
        if (header.StartsWith(JpegSignature)) return "image/jpeg";

        // Chuẩn PDF cho phép "%PDF-" nằm trong 1024 byte đầu (không nhất thiết ở byte 0)
        var limit = Math.Min(header.Length, HeaderLength);
        if (header[..limit].IndexOf(PdfSignature) >= 0) return "application/pdf";

        return null;
    }

    public static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };

    private static string FriendlyName(string contentType) => contentType switch
    {
        "application/pdf" => "PDF",
        "image/png" => "ảnh PNG",
        "image/jpeg" => "ảnh JPG",
        _ => "không xác định"
    };

    private static string ToMegabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.GetCultureInfo("vi-VN"));
}

/// <summary>Kết quả kiểm tra một bước.</summary>
public sealed record CvFileCheck(bool IsValid, string? Extension, string? ContentType, string? ErrorCode, string? ErrorMessage)
{
    public static CvFileCheck Ok(string extension, string contentType) => new(true, extension, contentType, null, null);
    public static CvFileCheck Fail(string code, string message) => new(false, null, null, code, message);
}
