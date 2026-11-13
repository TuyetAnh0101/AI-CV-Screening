namespace SmartCv.Core.Contracts;

/// <summary>
/// A10: kết quả chấm thử (POST /api/applications/{id}/ai-preview). Không có JSON gốc của AI vì API chưa có xác thực.
/// ScoreSource = KeywordOnly nghĩa là AI lỗi, điểm CHƯA có phần 70% AI. AiSimulated = true nghĩa là dùng AI giả.
/// </summary>
public sealed record AiPreviewDto(
    string PromptVersion, string Model, decimal KeywordScore, decimal? AiScore, decimal FinalScore,
    string ScoreSource, string FitLabel, bool PredictedPass, decimal? YearsExperience, string Reasoning,
    bool AiSucceeded, string AiErrorKind, string? AiErrorMessage, int AiAttempts, bool AiSimulated, bool AiCvTruncated,
    List<string> AiMatchedSkills, List<string> AiMissingSkills, List<string> AiInvalidSkillCodes,
    List<AiPreviewSkillDto> Skills)
{
    // Preview is not persisted: return the settings used by this request rather than
    // making clients assume that the default 30/70 was used.
    public decimal KeywordWeight { get; init; }
    public decimal AiWeight { get; init; }
    public decimal PassThreshold { get; init; }
    public decimal? MissingMustHaveCap { get; init; }

    // ----- A41: chấm thử prompt nháp / phiên bản đã lưu (mọi trường A10 ở trên giữ nguyên) -----

    /// <summary>Draft | Version | Active (xem PromptSourceKinds). PromptVersion ở trên là tên hiển thị ("Nháp (chưa lưu)" với bản nháp).</summary>
    public string PromptSource { get; init; } = PromptSourceKinds.Active;
    public int? PromptVersionId { get; init; }      // null với bản nháp
    public bool IsDraft { get; init; }

    /// <summary>Luôn false: kết quả chấm thử không được lưu và không thay đổi kết quả tuyển dụng.</summary>
    public bool Persisted => false;

    /// <summary>Số lượt chấm thử còn lại / tối đa trong cửa sổ, sau lần gọi này. ResetsAt (UTC): lúc lượt sớm nhất được trả lại.</summary>
    public int RemainingTries { get; init; }
    public int TryLimit { get; init; }
    public DateTime? QuotaResetsAt { get; init; }

    /// <summary>false khi lượt được trả lại vì chưa gửi gì tới AI (ví dụ thiếu API key).</summary>
    public bool TryCounted { get; init; }

    /// <summary>Kết quả ĐÃ LƯU gần nhất của đúng hồ sơ này (null nếu chưa có), để so cạnh kết quả chấm thử.</summary>
    public CurrentEvaluationDto? CurrentEvaluation { get; init; }
}

/// <summary>MatchedBy: Keyword (Evidence là trích dẫn từ CV), AI (chỉ Gemini xác nhận), null (không tìm thấy).</summary>
public sealed record AiPreviewSkillDto(string SkillCode, string DisplayName, string SkillType, bool IsMatched, string? MatchedBy, string? Evidence);
