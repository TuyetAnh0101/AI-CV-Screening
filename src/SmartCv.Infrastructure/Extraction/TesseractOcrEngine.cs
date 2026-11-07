using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Options;
using Tesseract;

namespace SmartCv.Infrastructure.Extraction;

/// <summary>Máy chủ thiếu file dữ liệu ngôn ngữ OCR (*.traineddata).</summary>
public sealed class OcrDataMissingException(string language)
    : Exception($"Thiếu dữ liệu OCR {language}.traineddata")
{
    public string Language { get; } = language;
}

/// <summary>
/// Bọc thư viện Tesseract (OCR). Đăng ký Singleton: tải mô hình ngôn ngữ một lần (mất khoảng 1 giây),
/// rồi dùng lại cho mọi CV. TesseractEngine KHÔNG an toàn khi nhiều luồng dùng cùng lúc, nên dùng khóa (lock).
/// Nếu khởi tạo lỗi (thiếu / hỏng tessdata) thì lần sau thử khởi tạo lại, nên chép tessdata vào là dùng được ngay.
/// </summary>
public sealed class TesseractOcrEngine(IOptions<OcrOptions> options, ILogger<TesseractOcrEngine> logger) : IDisposable
{
    private readonly OcrOptions _options = options.Value;
    private readonly Lock _lock = new();
    private TesseractEngine? _engine;

    /// <summary>Nhận dạng chữ trong một ảnh (PNG/JPG dạng byte). Trả về chữ và độ tin cậy trung bình (0–1).</summary>
    public (string Text, float Confidence) Recognize(byte[] imageBytes)
    {
        lock (_lock)
        {
            var engine = _engine ??= CreateEngine();
            using var image = Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(image);
            return (page.GetText() ?? string.Empty, page.GetMeanConfidence());
        }
    }

    private TesseractEngine CreateEngine()
    {
        var path = _options.TessdataPath;

        // Kiểm tra trước để báo lỗi dễ hiểu, thay vì lỗi chung chung của Tesseract.
        // Đường dẫn đầy đủ chỉ ghi vào log máy chủ, không đưa vào thông báo lưu trong database / trả qua API.
        foreach (var lang in _options.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(Path.Combine(path, $"{lang}.traineddata")))
            {
                logger.LogError("Thiếu {Lang}.traineddata trong {Path}. Chạy scripts/tai-tessdata.ps1", lang, path);
                throw new OcrDataMissingException(lang);
            }
        }

        logger.LogInformation("Khởi tạo Tesseract: ngôn ngữ {Lang}, dữ liệu tại {Path}", _options.Languages, path);
        return new TesseractEngine(path, _options.Languages, EngineMode.Default);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
