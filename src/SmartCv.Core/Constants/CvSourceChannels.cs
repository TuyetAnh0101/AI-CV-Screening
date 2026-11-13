namespace SmartCv.Core.Constants;

public static class CvSourceChannels
{
    public const string Email = "Email";
    public const string Website = "Website";
    public const string JobBoard = "JobBoard";
    public const string Referral = "Referral";
    public const string Other = "Other";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Email, Website, JobBoard, Referral, Other });
    public static bool IsValid(string? channel) => channel is not null && All.Contains(channel);
    public static string DisplayName(string? channel) => channel switch
    {
        Email => "Email ứng tuyển",
        Website => "Website công ty",
        JobBoard => "Trang tuyển dụng (TopCV, VietnamWorks...)",
        Referral => "Người quen giới thiệu",
        Other => "Khác",
        null => "Chưa ghi (CV tải trước A38)",
        _ => "Nguồn chưa xác định"
    };
}
