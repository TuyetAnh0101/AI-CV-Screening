namespace SmartCv.Core.Options;

/// <summary>
/// Cấu hình chấm điểm, mục "Scoring" trong appsettings.json.
/// A9 chỉ dùng các trọng số Keyword*. KeywordWeight / AiWeight (30% / 70%) dành cho bước gộp điểm ở A10;
/// A10, A11 bổ sung các thuộc tính riêng của chúng (giới hạn khi thiếu kỹ năng bắt buộc, chu kỳ chạy nền) vào lớp này.
/// </summary>
public sealed class ScoringOptions
{
    public const string SectionName = "Scoring";

    // ----- Lưu theo từng lượt chấm -----
    public string ModelName { get; set; } = "gemini-2.5-flash";
    public decimal Temperature { get; set; } = 0.2m;
    public decimal KeywordWeight { get; set; } = 0.3m;   // điểm cuối = 30% keyword + 70% AI (A10)
    public decimal AiWeight { get; set; } = 0.7m;
    public decimal PassThreshold { get; set; } = 70m;    // từ 70 điểm: dự đoán Đạt

    // ----- A9: trọng số BÊN TRONG điểm keyword 0–100 (tổng = 1), khác với tỷ lệ 30/70 ở trên -----
    public decimal KeywordMustHaveWeight { get; set; } = 0.6m;
    public decimal KeywordNiceToHaveWeight { get; set; } = 0.2m;
    public decimal KeywordGpaWeight { get; set; } = 0.1m;
    public decimal KeywordExperienceWeight { get; set; } = 0.1m;

    /// <summary>Kiểm tra lúc khởi động: mỗi nhóm trọng số không âm và có tổng đúng bằng 1.</summary>
    public bool HasValidWeights() =>
        new[] { KeywordWeight, AiWeight, KeywordMustHaveWeight, KeywordNiceToHaveWeight, KeywordGpaWeight, KeywordExperienceWeight }.All(w => w >= 0m)
        && KeywordWeight + AiWeight == 1m
        && KeywordMustHaveWeight + KeywordNiceToHaveWeight + KeywordGpaWeight + KeywordExperienceWeight == 1m;
}
