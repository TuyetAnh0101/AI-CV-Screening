using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;
using SmartCv.Core.Constants;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace SmartCv.Infrastructure.Extraction;

/// <summary>
/// A7: đọc chữ từ CV.
///   PDF  → PdfPig đọc lớp chữ. Đủ chữ: PdfText. Quá ít chữ (bản scan): đổi từng trang thành ảnh rồi OCR → PdfOcr.
///   Ảnh  → OCR Tesseract (eng+vie) → ImageOcr.
/// Thông báo lỗi trả về là câu cố định, dễ hiểu; KHÔNG chứa ex.Message hay đường dẫn trên máy chủ
/// (chi tiết kỹ thuật chỉ ghi vào log). Log không ghi tên file vì tên file thường chứa tên ứng viên.
/// </summary>
public sealed class CvTextExtractor(
    TesseractOcrEngine ocr,
    IOptions<OcrOptions> options,
    ILogger<CvTextExtractor> logger) : ICvTextExtractor
{
    public const string MissingOcrDataError =
        "Máy chủ chưa có dữ liệu OCR (tessdata). Quản trị viên cần chạy scripts/tai-tessdata.ps1 rồi bấm đọc lại.";

    private readonly OcrOptions _options = options.Value;

    public async Task<TextExtractionResult> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        // Đọc hết vào bộ nhớ (CV tối đa 10 MB): PdfPig và PDFtoImage đều cần đọc lại file nhiều lần
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        // OCR tốn CPU và không có bản async: chạy trên luồng nền để không chặn luồng xử lý request
        return await Task.Run(() => contentType switch
        {
            "application/pdf" => ExtractPdf(bytes, cancellationToken),
            "image/png" or "image/jpeg" => ExtractImage(bytes),
            _ => TextExtractionResult.Failure("Không hỗ trợ đọc chữ từ loại file này.")
        }, cancellationToken);
    }

    // ---------------- PDF ----------------
    private TextExtractionResult ExtractPdf(byte[] bytes, CancellationToken ct)
    {
        string text;
        int pageCount;
        var pageSizes = new List<(double Width, double Height)>();   // đơn vị point (1/72 inch)

        try
        {
            using var document = PdfDocument.Open(bytes);
            pageCount = document.NumberOfPages;

            var sb = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                if (pageSizes.Count < _options.MaxOcrPages) pageSizes.Add((page.Width, page.Height));
                // ContentOrderTextExtractor: đọc theo thứ tự tự nhiên (trên xuống, trái sang), giữ xuống dòng
                sb.AppendLine(ContentOrderTextExtractor.GetText(page));
            }
            text = TextCleaner.Clean(sb.ToString());
        }
        catch (PdfDocumentEncryptedException)
        {
            return TextExtractionResult.Failure("Không đọc được nội dung: file PDF có mật khẩu.", ExtractionMethods.PdfText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "PdfPig không mở được PDF");
            return TextExtractionResult.Failure("Không đọc được nội dung: file PDF bị hỏng hoặc có mật khẩu.", ExtractionMethods.PdfText);
        }

        if (pageCount == 0)
            return TextExtractionResult.Failure("Không đọc được nội dung: file PDF không có trang nào.", ExtractionMethods.PdfText);

        // Đủ chữ → PDF có lớp chữ thật
        var chars = TextCleaner.CountMeaningfulChars(text);
        if (chars >= _options.MinCharsPerPage * pageCount)
            return TextExtractionResult.Success(text, ExtractionMethods.PdfText, pageCount);

        // Quá ít chữ → bản scan (mỗi trang chỉ là một tấm ảnh) → OCR
        logger.LogInformation("PDF có {Chars} ký tự / {Pages} trang, coi là bản scan, chuyển sang OCR", chars, pageCount);
        return OcrPdf(bytes, pageCount, pageSizes, ct);
    }

    private TextExtractionResult OcrPdf(byte[] bytes, int pageCount, List<(double Width, double Height)> pageSizes, CancellationToken ct)
    {
        // PDFtoImage dùng thư viện PDFium, chỉ có cho Windows, Linux, macOS
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            return TextExtractionResult.Failure("Máy chủ không hỗ trợ đọc PDF scan.", ExtractionMethods.PdfOcr);

        var sb = new StringBuilder();
        var pagesToRead = Math.Min(pageCount, _options.MaxOcrPages);

        try
        {
            for (var i = 0; i < pagesToRead; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Trang khổ rất lớn ở 300 DPI có thể thành ảnh hàng trăm triệu điểm ảnh: hạ DPI cho vừa giới hạn
                var dpi = SafeDpi(pageSizes[i].Width, pageSizes[i].Height);
                using var bitmap = Conversion.ToImage(bytes, page: (Index)i, options: new RenderOptions(Dpi: dpi));
                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                var (pageText, confidence) = ocr.Recognize(png.ToArray());
                logger.LogInformation("OCR trang {Page}/{Total} ({Dpi} DPI): độ tin cậy {Conf:P0}", i + 1, pagesToRead, dpi, confidence);
                sb.AppendLine(pageText);
            }
        }
        catch (OcrDataMissingException)
        {
            return TextExtractionResult.Failure(MissingOcrDataError, ExtractionMethods.PdfOcr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Lỗi khi OCR PDF scan");
            return TextExtractionResult.Failure("Lỗi khi đọc PDF scan bằng OCR.", ExtractionMethods.PdfOcr);
        }

        return Finish(sb.ToString(), ExtractionMethods.PdfOcr, pageCount);
    }

    private int SafeDpi(double widthPt, double heightPt)
    {
        var inches2 = widthPt / 72d * (heightPt / 72d);
        if (inches2 <= 0) return _options.RenderDpi;
        var maxDpi = (int)Math.Sqrt(_options.MaxImagePixels / inches2);
        return Math.Max(1, Math.Min(_options.RenderDpi, maxDpi));
    }

    // ---------------- Ảnh ----------------
    private TextExtractionResult ExtractImage(byte[] bytes)
    {
        // Đọc kích thước từ phần đầu ảnh (không giải nén cả ảnh) để chặn "bom giải nén":
        // file PNG vài MB có thể chứa ảnh hàng tỉ điểm ảnh, OCR sẽ ngốn hết RAM.
        using (var codec = SKCodec.Create(new SKMemoryStream(bytes)))
        {
            if (codec is null)
                return TextExtractionResult.Failure("Không đọc được nội dung: file ảnh bị hỏng.", ExtractionMethods.ImageOcr);
            if ((long)codec.Info.Width * codec.Info.Height > _options.MaxImagePixels)
                return TextExtractionResult.Failure("Ảnh có độ phân giải quá lớn, không đọc được. Hãy chụp/xuất lại ảnh nhỏ hơn.", ExtractionMethods.ImageOcr);
        }

        try
        {
            var (text, confidence) = ocr.Recognize(bytes);
            logger.LogInformation("OCR ảnh: độ tin cậy {Conf:P0}", confidence);
            return Finish(text, ExtractionMethods.ImageOcr, 1);
        }
        catch (OcrDataMissingException)
        {
            return TextExtractionResult.Failure(MissingOcrDataError, ExtractionMethods.ImageOcr);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lỗi khi OCR ảnh");
            return TextExtractionResult.Failure("Lỗi khi đọc ảnh bằng OCR.", ExtractionMethods.ImageOcr);
        }
    }

    private static TextExtractionResult Finish(string rawText, string method, int pageCount)
    {
        var text = TextCleaner.Clean(rawText);
        return TextCleaner.CountMeaningfulChars(text) < 20
            ? TextExtractionResult.Failure("Không nhận ra chữ trong file (ảnh mờ, quá nhỏ hoặc không có chữ).", method)
            : TextExtractionResult.Success(text, method, pageCount);
    }
}
