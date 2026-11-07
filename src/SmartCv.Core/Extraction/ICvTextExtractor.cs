namespace SmartCv.Core.Extraction;

/// <summary>Đọc chữ từ file CV: PDF có chữ (PdfPig), PDF scan và ảnh (OCR Tesseract).</summary>
public interface ICvTextExtractor
{
    Task<TextExtractionResult> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default);
}

/// <summary>Kết quả đọc chữ. Method khớp ràng buộc CK_Cvs_ExtractionMethod: PdfText | PdfOcr | ImageOcr.</summary>
public sealed record TextExtractionResult(bool IsSuccess, string? Text, string? Method, string? Error, int PageCount = 0)
{
    public static TextExtractionResult Success(string text, string method, int pageCount) => new(true, text, method, null, pageCount);
    public static TextExtractionResult Failure(string error, string? method = null) => new(false, null, method, error);
}
