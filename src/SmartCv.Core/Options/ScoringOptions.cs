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
    /// <summary>
    /// Model Gemini dùng để chấm: NGUỒN DUY NHẤT (xem trước A10, lượt chấm A11 chụp lại giá trị này, kiểm tra /api/health/gemini).
    /// Đổi bằng appsettings "Scoring:ModelName" hoặc biến môi trường Scoring__ModelName (ứng dụng KHÔNG đọc GEMINI_MODEL).
    /// gemini-2.5-flash đã bị Google gỡ với người dùng mới (HTTP 404); gemini-3.8-flash từng trả 503 "high demand" liên tục.
    /// Giá trị này chỉ dùng khi thiếu cấu hình; phải giống appsettings.json (test GeminiModelConfigurationTests).
    /// </summary>
    public string ModelName { get; set; } = "gemini-3.1-flash-lite";
    public decimal Temperature { get; set; } = 0.2m;
    public decimal KeywordWeight { get; set; } = 0.3m;   // điểm cuối = 30% keyword + 70% AI (A10)
    public decimal AiWeight { get; set; } = 0.7m;
    public decimal PassThreshold { get; set; } = 70m;    // từ 70 điểm: dự đoán Đạt

    // ----- A9: trọng số BÊN TRONG điểm keyword 0–100 (tổng = 1), khác với tỷ lệ 30/70 ở trên -----
    public decimal KeywordMustHaveWeight { get; set; } = 0.6m;
    public decimal KeywordNiceToHaveWeight { get; set; } = 0.2m;
    public decimal KeywordGpaWeight { get; set; } = 0.1m;
    public decimal KeywordExperienceWeight { get; set; } = 0.1m;

    /// <summary>
    /// A10: thiếu kỹ năng bắt buộc thì điểm cuối không vượt mức này. Giá trị 69 đặt trong appsettings.json.
    /// Mặc định của lớp phải là null: khi cấu hình ghi "MissingMustHaveCap": null, binder bỏ qua giá trị null
    /// và giữ mặc định, nên chỉ mặc định null mới tắt được quy tắc này.
    /// </summary>
    public decimal? MissingMustHaveCap { get; set; }

    // ----- A11: chạy nền -----
    /// <summary>
    /// Bật tiến trình chấm nền. Mặc định TẮT: khi bật, worker tự chấm mọi lượt Queued/Running của database đang kết nối
    /// và có thể tốn quota Gemini ngay lúc API khởi động, nên phải bật có chủ đích.
    /// </summary>
    public bool WorkerEnabled { get; set; }

    /// <summary>Không có việc thì cứ bao nhiêu giây kiểm tra lại database một lần.</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Kiểm tra lúc khởi động: ngưỡng đạt và giới hạn điểm nằm trong thang 0–100.</summary>
    public bool HasValidScoreLimits() =>
        PassThreshold is >= 0m and <= 100m && MissingMustHaveCap is null or (>= 0m and <= 100m);

    /// <summary>Kiểm tra lúc khởi động: mỗi nhóm trọng số không âm và có tổng đúng bằng 1.</summary>
    public bool HasValidWeights() =>
        new[] { KeywordWeight, AiWeight, KeywordMustHaveWeight, KeywordNiceToHaveWeight, KeywordGpaWeight, KeywordExperienceWeight }.All(w => w >= 0m)
        && KeywordWeight + AiWeight == 1m
        && KeywordMustHaveWeight + KeywordNiceToHaveWeight + KeywordGpaWeight + KeywordExperienceWeight == 1m;
}
