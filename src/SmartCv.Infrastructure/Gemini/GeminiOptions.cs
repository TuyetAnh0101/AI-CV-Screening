using System.ComponentModel.DataAnnotations;

namespace SmartCv.Infrastructure.Gemini;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>Bắt buộc khi gọi Gemini thật; kiểm tra lúc khởi động trong AddInfrastructure. Chỉ đặt bằng User Secrets.</summary>
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    public string Model { get; set; } = "gemini-3.1-flash-lite";

    [Range(0.0, 2.0)]
    public double Temperature { get; set; } = 0.2;

    [Range(1000, 100000)]
    public int MaxCvCharacters { get; set; } = 12000;

    /// <summary>Số lần THỬ LẠI khi lỗi tạm thời. 2 → tối đa 3 lần gọi.</summary>
    [Range(0, 5)]
    public int MaxRetries { get; set; } = 2;

    // ----- A10 -----
    [Required]
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";

    /// <summary>Khoảng cách tối thiểu giữa 2 lần gọi (ms), để không vượt hạn mức gọi/phút.</summary>
    [Range(0, 600000)]
    public int MinDelayBetweenCallsMs { get; set; } = 4500;

    /// <summary>Thời gian chờ tối đa mỗi lần gọi.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>Chờ tối đa bao lâu trước khi thử lại, kể cả khi Gemini yêu cầu chờ lâu hơn (retryDelay).</summary>
    [Range(0, 3600)]
    public int MaxRetryDelaySeconds { get; set; } = 60;

    /// <summary>Số lần gọi POST ai-preview tối đa mỗi phút cho một địa chỉ IP (mỗi lần tốn quota Gemini).</summary>
    [Range(1, 1000)]
    public int PreviewPermitPerMinute { get; set; } = 10;

    /// <summary>true: dùng AI GIẢ (không gọi mạng). Chỉ được bật ở Development, nơi khác ứng dụng dừng lúc khởi động.</summary>
    public bool UseFake { get; set; }

    /// <summary>Thời gian giả lập một lần gọi của AI giả.</summary>
    [Range(0, 60000)]
    public int FakeDelayMs { get; set; } = 800;
}
