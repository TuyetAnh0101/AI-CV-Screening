namespace SmartCv.Core.Options;

/// <summary>Cấu hình đọc chữ (A7), mục "TextExtraction" trong appsettings.json.</summary>
public sealed class OcrOptions
{
    public const string SectionName = "TextExtraction";

    /// <summary>Thư mục chứa eng.traineddata và vie.traineddata (tính từ thư mục dự án Api).</summary>
    public string TessdataPath { get; set; } = "tessdata";

    /// <summary>Ngôn ngữ OCR. "eng+vie": đọc cả tiếng Anh và tiếng Việt có dấu.</summary>
    public string Languages { get; set; } = "eng+vie";

    /// <summary>
    /// PDF có ít hơn số ký tự này (không tính khoảng trắng) trên mỗi trang thì coi là bản scan → chuyển sang OCR.
    /// </summary>
    public int MinCharsPerPage { get; set; } = 50;

    /// <summary>Độ phân giải khi chuyển trang PDF scan thành ảnh để OCR. 300 DPI là mức khuyến nghị của Tesseract.</summary>
    public int RenderDpi { get; set; } = 300;

    /// <summary>Số trang tối đa được OCR mỗi CV (OCR chậm, CV thường 1–3 trang).</summary>
    public int MaxOcrPages { get; set; } = 5;

    /// <summary>
    /// Số điểm ảnh tối đa của một ảnh đưa vào OCR (rộng × cao). Mặc định 40 triệu (khoảng A4 ở 600 DPI).
    /// Chặn "bom giải nén": file ảnh nhỏ nhưng khai báo kích thước cực lớn. Trang PDF scan khổ lớn được hạ DPI cho vừa.
    /// </summary>
    public long MaxImagePixels { get; set; } = 40_000_000;
}
