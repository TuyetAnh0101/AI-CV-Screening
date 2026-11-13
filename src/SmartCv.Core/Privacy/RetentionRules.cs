using SmartCv.Core.Constants;

namespace SmartCv.Core.Privacy;

public static class RetentionRules
{
    public static DateTime? RetainUntilAfterStageChange(string toStage, DateTime changedAtUtc, int months, DateTime? currentRetainUntil)
    {
        if (months is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(months), "Thời hạn phải từ 1 đến 120 tháng.");
        if (toStage is not (ApplicationStages.Rejected or ApplicationStages.OnHold)) return currentRetainUntil;
        var proposed = changedAtUtc.AddMonths(months);
        return currentRetainUntil is { } current && current > proposed ? current : proposed;
    }
}
