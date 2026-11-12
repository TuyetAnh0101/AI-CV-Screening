namespace SmartCv.Core.Constants;

/// <summary>Giai đoạn do HR quyết định, độc lập với điểm và đề xuất của AI.</summary>
public static class ApplicationStages
{
    public const string New = "New";
    public const string Reviewing = "Reviewing";
    public const string Shortlisted = "Shortlisted";
    public const string Interview = "Interview";
    public const string Rejected = "Rejected";
    public const string OnHold = "OnHold";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
        { New, Reviewing, Shortlisted, Interview, Rejected, OnHold });
    public static bool IsValid(string? stage) => stage is not null && All.Contains(stage);
    public static string DisplayName(string? stage) => stage switch
    {
        New => "Mới", Reviewing => "Đang xem", Shortlisted => "Qua vòng CV",
        Interview => "Phỏng vấn", Rejected => "Loại", OnHold => "Dự phòng",
        _ => stage ?? string.Empty
    };
}

public static class RejectReasons
{
    public const string MissingSkills = "MissingSkills";
    public const string NotEnoughExperience = "NotEnoughExperience";
    public const string OtherRequirement = "OtherRequirement";
    public const string CandidateWithdrew = "CandidateWithdrew";
    public const string PositionFilled = "PositionFilled";
    public const string Duplicate = "Duplicate";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
        { MissingSkills, NotEnoughExperience, OtherRequirement, CandidateWithdrew, PositionFilled, Duplicate });
    public static bool IsValid(string? reason) => reason is not null && All.Contains(reason);
    public static string DisplayName(string? reason) => reason switch
    {
        MissingSkills => "Thiếu kỹ năng bắt buộc", NotEnoughExperience => "Chưa đủ kinh nghiệm",
        OtherRequirement => "Không phù hợp yêu cầu khác", CandidateWithdrew => "Ứng viên rút hồ sơ",
        PositionFilled => "Đã tuyển đủ", Duplicate => "Hồ sơ trùng",
        _ => reason ?? string.Empty
    };
}

// Các giá trị chữ phải khớp đúng với ràng buộc CHECK trong database.
// Dùng hằng số thay vì gõ tay "Pending", "Completed"... để tránh gõ sai chính tả.

public static class SkillTypes
{
    public const string MustHave = "MustHave";
    public const string NiceToHave = "NiceToHave";
}

public static class ExtractionMethods
{
    public const string PdfText = "PdfText";
    public const string PdfOcr = "PdfOcr";
    public const string ImageOcr = "ImageOcr";
}

public static class ExtractionStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class RunTypes
{
    public const string Screening = "Screening";
    public const string PromptTest = "PromptTest";
}

public static class RunStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class EvaluationStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class ScoreSources
{
    public const string Combined = "Combined";
    public const string KeywordOnly = "KeywordOnly";
}
