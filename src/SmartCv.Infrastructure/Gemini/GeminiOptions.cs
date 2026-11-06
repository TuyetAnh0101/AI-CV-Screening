using System.ComponentModel.DataAnnotations;

namespace SmartCv.Infrastructure.Gemini;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    [Required(ErrorMessage = "Chưa cấu hình Gemini:ApiKey. Xem README, mục User Secrets.")]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    public string Model { get; set; } = "gemini-3.1-flash-lite";

    [Range(0.0, 2.0)]
    public double Temperature { get; set; } = 0.2;

    [Range(1000, 100000)]
    public int MaxCvCharacters { get; set; } = 12000;

    [Range(0, 5)]
    public int MaxRetries { get; set; } = 2;
}