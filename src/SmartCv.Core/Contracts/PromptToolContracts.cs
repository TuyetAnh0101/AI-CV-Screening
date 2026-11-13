namespace SmartCv.Core.Contracts;

// A41: công cụ cho trang System prompt (B39 "Thử với hồ sơ…"). Chỉ có ở Development khi CvUpload:Endpoint:Enabled = true (đến M6).
//   POST /api/applications/{id}/ai-preview     chấm thử, KHÔNG lưu. Body không bắt buộc: AiPreviewRequest. Trả AiPreviewDto (A10 + A41).
//   GET  /api/applications/ai-preview/quota    số lượt chấm thử còn lại (PreviewQuotaDto), không tốn lượt
//   POST /api/prompts/assembled-preview        nội dung SẼ gửi Gemini (AssembledPromptDto): không gọi Gemini, không tốn lượt, không lưu

/// <summary>Nguồn system prompt của một lần chấm thử / xem trước.</summary>
public static class PromptSourceKinds
{
    public const string Draft = "Draft";       // bản nháp chưa lưu (systemPrompt trong body)
    public const string Version = "Version";   // một phiên bản đã lưu (promptVersionId trong body)
    public const string Active = "Active";     // không chọn gì → phiên bản đang kích hoạt (hành vi A10)
}

/// <summary>
/// Body của ai-preview. Không gửi body (hoặc {}) → bản đang kích hoạt, giống A10.
/// Chỉ được chọn MỘT trong hai: SystemPrompt (nháp, tối đa 20.000 ký tự sau khi bỏ khoảng trắng đầu/cuối) hoặc PromptVersionId.
/// </summary>
public sealed class AiPreviewRequest
{
    public string? SystemPrompt { get; set; }
    public int? PromptVersionId { get; set; }
}

/// <summary>Body của assembled-preview: hồ sơ cần xem + nguồn prompt (cùng quy tắc với <see cref="AiPreviewRequest"/>).</summary>
public sealed class AssembledPreviewRequest
{
    public int ApplicationId { get; set; }
    public string? SystemPrompt { get; set; }
    public int? PromptVersionId { get; set; }
}

/// <summary>
/// Kết quả chấm ĐÃ LƯU gần nhất (Evaluations.Status = Completed) của đúng hồ sơ đó, ở lượt Screening hoặc PromptTest,
/// để giao diện đặt cạnh kết quả chấm thử. Không bao giờ bị kết quả chấm thử ghi đè.
/// </summary>
public sealed class CurrentEvaluationDto
{
    public int EvaluationId { get; init; }
    public int RunId { get; init; }
    public string RunType { get; init; } = string.Empty;           // Screening | PromptTest
    public int PromptVersionId { get; init; }
    public string PromptVersionName { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public decimal? KeywordScore { get; init; }
    public decimal? AiScore { get; init; }
    public decimal? FinalScore { get; init; }
    public string? ScoreSource { get; init; }                      // Combined | KeywordOnly
    public string? FitLabel { get; init; }
    public bool? PredictedPass { get; init; }
    public decimal? YearsExperience { get; init; }
    public string? Reasoning { get; init; }
    public List<string> MatchedSkills { get; init; } = [];
    public List<string> MissingSkills { get; init; } = [];
    public DateTime CreatedAt { get; init; }                       // UTC: lúc tạo kết quả trong lượt chấm
    public DateTime? RunCompletedAt { get; init; }                 // UTC: lúc lượt chấm xong (null khi lượt còn chạy)
}

/// <summary>
/// Số lượt chấm thử (cả ứng dụng, cửa sổ trượt). ResetsAt (UTC): lúc lượt dùng sớm nhất trong cửa sổ được trả lại; null khi chưa dùng lượt nào.
/// AiSimulated = true: đang dùng AI GIẢ (Gemini:UseFake), lượt thử vẫn được đếm để thử giao diện nhưng KHÔNG tốn hạn mức Gemini.
/// </summary>
public sealed class PreviewQuotaDto
{
    public int Remaining { get; init; }
    public int Limit { get; init; }
    public int WindowMinutes { get; init; }
    public DateTime? ResetsAt { get; init; }
    public int InFlight { get; init; }          // số lượt chấm thử đang chạy
    public int MaxConcurrent { get; init; }     // tối đa chạy cùng lúc; vượt → 429 "đang bận"
    public bool AiSimulated { get; init; }
}

/// <summary>
/// Toàn bộ nội dung SẼ gửi Gemini cho một hồ sơ (cùng hàm AiPromptAssembler với lượt chấm thật).
/// Chỉ trả khi Gemini:MaskPersonalData = true: UserPrompt chứa CV ĐÃ CHE ([CANDIDATE], [EMAIL], [PHONE], [LINK], [DOB], [GENDER]).
/// AiSimulated = true: máy chủ đang dùng AI giả nên thực tế không gửi gì tới Gemini; nội dung là bản sẽ gửi khi dùng Gemini thật.
/// </summary>
public sealed class AssembledPromptDto
{
    public int ApplicationId { get; init; }
    public int JobId { get; init; }
    public string JobTitle { get; init; } = string.Empty;
    public string PromptSource { get; init; } = string.Empty;      // Draft | Version | Active
    public int? PromptVersionId { get; init; }                     // null với bản nháp
    public string PromptVersion { get; init; } = string.Empty;     // "Nháp (chưa lưu)" hoặc tên phiên bản
    public bool IsDraft { get; init; }
    public string Model { get; init; } = string.Empty;
    public decimal Temperature { get; init; }
    public bool AiSimulated { get; init; }
    public string SystemPrompt { get; init; } = string.Empty;
    public string UserPrompt { get; init; } = string.Empty;        // thông tin job + quy tắc + CV đã che và đã cắt
    public bool PersonalDataMasked { get; init; }
    public int MaskedNames { get; init; }
    public int MaskedEmails { get; init; }
    public int MaskedPhones { get; init; }
    public int MaskedLinks { get; init; }
    public int MaskedBirthDates { get; init; }
    public int MaskedGenders { get; init; }
    public int MaskedTotal { get; init; }
    public string NameMaskSource { get; init; } = string.Empty;    // Provided | Label | Header | Fallback | None (None: không xác định được họ tên để che)
    public int CvCharacters { get; init; }                         // độ dài CV gốc
    public int CvCharactersSent { get; init; }                     // độ dài phần CV nằm trong UserPrompt
    public int MaxCvCharacters { get; init; }
    public bool Truncated { get; init; }
    public List<string> AllowedSkillCodes { get; init; } = [];     // Gemini chỉ được trả các mã này (responseSchema)
}
