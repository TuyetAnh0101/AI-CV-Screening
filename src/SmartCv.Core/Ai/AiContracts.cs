using SmartCv.Core.Scoring;

namespace SmartCv.Core.Ai;

/// <summary>Chấm một CV bằng AI. Bản thật: GeminiEvaluationService. Bản giả để thử: FakeAiEvaluator.</summary>
public interface IAiEvaluator
{
    Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Dữ liệu gửi AI. Model, Temperature và SystemPrompt do NGƯỜI GỌI truyền vào, service không tự đọc prompt đang kích hoạt:
/// xem trước (A10) dùng prompt đang kích hoạt, lượt chấm chính thức (A11/A12) dùng giá trị đã chụp lúc tạo lượt.
/// CvText là nội dung GỐC; bản gửi Gemini thật được che thông tin cá nhân trong GeminiEvaluationService (A37).
/// CandidateName (A37): họ tên ứng viên từ bảng Candidates để che trong CV; null khi CV chưa gắn ứng viên (khi đó đoán từ CV).
/// </summary>
public sealed record AiEvaluationRequest(
    string Model,
    decimal Temperature,
    string SystemPrompt,
    JobProfile Job,
    string CvText,
    string? CandidateName = null);

/// <summary>
/// Loại lỗi AI (không lưu database; database chỉ lưu ErrorMessage). Thêm ở CUỐI để giữ giá trị số cũ.
/// Network: mất kết nối, HTTP 500/502/504. Overloaded: HTTP 503 (Gemini quá tải, "high demand").
/// Timeout: Gemini không phản hồi trong TimeoutSeconds. ModelUnavailable: HTTP 404, model không tồn tại hoặc đã bị gỡ.
/// </summary>
public enum AiErrorKind { None, QuotaExceeded, Network, InvalidResponse, Blocked, Configuration, InvalidRequest, Other, Overloaded, Timeout, ModelUnavailable }

/// <summary>Kết quả AI. Kỹ năng trả về theo MÃ (SkillCode) cố định, không theo tên tự do.</summary>
public sealed record AiEvaluationResult
{
    public bool IsSuccess { get; init; }
    public decimal? Score { get; init; }
    public IReadOnlyList<string> MatchedSkillCodes { get; init; } = [];
    public IReadOnlyList<string> MissingSkillCodes { get; init; } = [];

    /// <summary>Mã AI trả về nhưng không thuộc job: đã loại, giữ lại để kiểm thử chất lượng AI.</summary>
    public IReadOnlyList<string> InvalidSkillCodes { get; init; } = [];

    public decimal? YearsExperience { get; init; }
    public decimal? Gpa { get; init; }
    public string? Reasoning { get; init; }

    /// <summary>JSON gốc AI trả về, để A11/A12 lưu vào Evaluations.AiRawResponse. Không ghi log, không trả qua API xem trước.</summary>
    public string? RawResponse { get; init; }

    public AiErrorKind ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }
    public int Attempts { get; init; }

    /// <summary>true: CV dài hơn giới hạn nên AI chỉ đọc phần đầu, kỹ năng ở cuối CV có thể bị bỏ sót.</summary>
    public bool CvTruncated { get; init; }

    /// <summary>true: kết quả của AI GIẢ (FakeAiEvaluator), không phải Gemini đánh giá.</summary>
    public bool IsSimulated { get; init; }

    public static AiEvaluationResult Failure(AiErrorKind kind, string message, int attempts, string? raw = null) =>
        new() { IsSuccess = false, ErrorKind = kind, ErrorMessage = message, Attempts = attempts, RawResponse = raw };
}
