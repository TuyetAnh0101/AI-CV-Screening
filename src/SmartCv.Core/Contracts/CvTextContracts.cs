namespace SmartCv.Core.Contracts;

// A42: hỗ trợ trang Tải CV (B38). Chỉ có ở Development khi CvUpload:Endpoint:Enabled = true (đến M6).
//   GET /api/cvs/{id}/text            chữ đã đọc + chất lượng (CvTextDto); 410 khi CV đã ẩn danh hóa
//   GET /api/cvs/{id}                 thêm TextQuality (CvDetailDto)
//   POST /api/cvs/upload              file trùng CV cũ: thêm Existing* (CvUploadItemResult)
//   GET /api/runs/estimate?count=N    thời gian chấm dự kiến (RunEstimateDto)

/// <summary>
/// Chỉ số chất lượng chữ đọc được (TextQuality.Assess). Level: Good | Low | Empty.
/// MaybeMisread = true → giao diện hiện cảnh báo vàng "có thể đọc sai". Warnings: câu tiếng Việt, chỉ chứa con số.
/// </summary>
public sealed class TextQualityDto
{
    public int CharCount { get; init; }
    public int WordCount { get; init; }
    public decimal LetterRatio { get; init; }                 // 0–1 (không phải %), cắt còn 2 chữ số
    public string Level { get; init; } = string.Empty;
    public bool MaybeMisread { get; init; }
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// GET /api/cvs/{id}/text: chữ đã đọc từ CV để HR kiểm tra trước khi chấm.
/// Text và Quality chỉ có khi ExtractionStatus = Completed; Pending / Failed trả null (không bịa chất lượng),
/// StatusMessage giải thích bằng tiếng Việt. Không có đường dẫn lưu file.
/// </summary>
public sealed class CvTextDto
{
    public Guid CvId { get; init; }
    public string OriginalFileName { get; init; } = string.Empty;
    public string ExtractionStatus { get; init; } = string.Empty;   // Pending | Completed | Failed
    public string? ExtractionMethod { get; init; }                  // PdfText | PdfOcr | ImageOcr
    public string? ExtractionError { get; init; }
    public string? Text { get; init; }
    public TextQualityDto? Quality { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
}

/// <summary>
/// GET /api/runs/estimate?count=N: thời gian chấm dự kiến, tính từ tối đa 10 lượt Screening đã xong gần nhất.
/// Chỉ là ước tính (phụ thuộc hàng đợi, thử lại khi Gemini quá tải), không phải cam kết thời gian.
/// </summary>
public sealed class RunEstimateDto
{
    public int Count { get; init; }
    public decimal SecondsPerCv { get; init; }        // 1 chữ số thập phân
    public int EstimatedSeconds { get; init; }        // làm tròn lên
    public int EstimatedMinutes { get; init; }        // làm tròn lên
    public int BasedOnRuns { get; init; }             // số lượt cũ thực sự dùng (0: giá trị mặc định)
    public bool IsDefault { get; init; }
    public decimal MinSecondsPerCv { get; init; }     // sàn: khoảng cách tối thiểu giữa hai lần gọi AI
    public bool AiSimulated { get; init; }            // true: đang dùng AI giả (Gemini:UseFake), không phản ánh Gemini thật
    public string Text { get; init; } = string.Empty;  // "10 CV · khoảng 2 phút"
    public string Note { get; init; } = string.Empty;  // giải thích nguồn số liệu, tiếng Việt
}
