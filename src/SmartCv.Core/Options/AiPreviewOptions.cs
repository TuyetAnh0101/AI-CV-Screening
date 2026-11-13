namespace SmartCv.Core.Options;

/// <summary>
/// A41: giới hạn số lượt chấm thử (POST /api/applications/{id}/ai-preview), mục "AiPreview" trong appsettings.json.
/// Mỗi lượt chấm thử bắt đầu gọi AI tốn một lượt; cửa sổ trượt dùng chung cả ứng dụng (chưa có tài khoản trước M6).
/// Bộ đếm nằm trong bộ nhớ của MỘT tiến trình API: chạy nhiều instance thì mỗi instance đếm riêng.
/// Sai cấu hình thì ứng dụng dừng ngay lúc khởi động (Program.cs), không tự sửa giá trị.
/// </summary>
public sealed class AiPreviewOptions
{
    public const string SectionName = "AiPreview";

    public const int MaxLimitPerWindow = 1000;
    public const int MaxWindowMinutes = 1440;
    public const int MaxConcurrentLimit = 20;

    /// <summary>Số lượt chấm thử tối đa trong một cửa sổ (cả ứng dụng), từ 1 đến <see cref="MaxLimitPerWindow"/>.</summary>
    public int LimitPerWindow { get; set; } = 30;

    /// <summary>Độ dài cửa sổ trượt (phút), từ 1 đến <see cref="MaxWindowMinutes"/> (24 giờ).</summary>
    public int WindowMinutes { get; set; } = 60;

    /// <summary>Số lượt chấm thử được chạy cùng lúc, từ 1 đến <see cref="MaxConcurrentLimit"/>; vượt thì trả 429 ngay, không xếp hàng.</summary>
    public int MaxConcurrent { get; set; } = 2;
}
