using SmartCv.Core.Constants;

namespace SmartCv.Core.Privacy;

public static class RetentionRules
{
    public const string AnonymizedFileName = "Hồ sơ đã ẩn danh";
    public static IReadOnlyList<string> ActiveStages { get; } = Array.AsReadOnly(new[]
        { ApplicationStages.New, ApplicationStages.Reviewing, ApplicationStages.Shortlisted, ApplicationStages.Interview });

    public static string? ManualBlockReason(IEnumerable<string> stages) =>
        stages.FirstOrDefault(s => s is ApplicationStages.Shortlisted or ApplicationStages.Interview) is { } stage
            ? $"Hồ sơ đang ở {ApplicationStages.DisplayName(stage)}. Chuyển sang Loại hoặc Dự phòng trước khi ẩn danh hóa."
            : null;

    public static bool IsDueForAnonymization(DateTime? until, DateTime? anonymizedAt, IEnumerable<string> stages, DateTime nowUtc) =>
        anonymizedAt is null && until is { } deadline && deadline <= nowUtc && !stages.Any(ActiveStages.Contains);

    public static DateTime? RetainUntilAfterStageChange(string toStage, DateTime changedAtUtc, int months, DateTime? currentRetainUntil)
    {
        if (months is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(months), "Thời hạn phải từ 1 đến 120 tháng.");
        if (toStage is not (ApplicationStages.Rejected or ApplicationStages.OnHold)) return currentRetainUntil;
        var proposed = changedAtUtc.AddMonths(months);
        return currentRetainUntil is { } current && current > proposed ? current : proposed;
    }
}
