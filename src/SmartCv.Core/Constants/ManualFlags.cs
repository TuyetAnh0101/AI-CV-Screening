namespace SmartCv.Core.Constants;

/// <summary>SCApp v2: trạng thái cờ thủ công của HR.</summary>
public static class ManualFlagStatuses
{
    public const string Open = "Open";
    public const string Resolved = "Resolved";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Open, Resolved });
    public static string DisplayName(string? status) => status switch
    {
        Open => "Đang cần kiểm tra", Resolved => "Đã xử lý", _ => status ?? string.Empty
    };
}

/// <summary>SCApp v2: lý do HR đánh dấu hồ sơ cần kiểm tra lại. Lý do "Other" bắt buộc có ghi chú.</summary>
public static class ManualFlagReasons
{
    public const string ScoreDoubt = "ScoreDoubt";
    public const string ExtractionIssue = "ExtractionIssue";
    public const string MissingInformation = "MissingInformation";
    public const string SecondOpinion = "SecondOpinion";
    public const string VerifyRequirement = "VerifyRequirement";
    public const string Other = "Other";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
        { ScoreDoubt, ExtractionIssue, MissingInformation, SecondOpinion, VerifyRequirement, Other });
    public static bool IsValid(string? reason) => reason is not null && All.Contains(reason);
    public static string DisplayName(string? reason) => reason switch
    {
        ScoreDoubt => "Điểm/nhận định AI chưa hợp lý",
        ExtractionIssue => "Nội dung trích xuất sai hoặc thiếu",
        MissingInformation => "CV thiếu thông tin cần thiết",
        SecondOpinion => "Cần người khác xem lại",
        VerifyRequirement => "Cần xác minh yêu cầu bắt buộc",
        Other => "Lý do khác",
        _ => reason ?? string.Empty
    };
}

/// <summary>SCApp v2: các loại sự kiện trong lịch sử cờ.</summary>
public static class ManualFlagActions
{
    public const string Opened = "Opened";
    public const string Resolved = "Resolved";
    public const string Reopened = "Reopened";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Opened, Resolved, Reopened });
    public static string DisplayName(string? action) => action switch
    {
        Opened => "Đánh dấu cần kiểm tra", Resolved => "Đánh dấu đã xử lý", Reopened => "Mở lại", _ => action ?? string.Empty
    };
}
