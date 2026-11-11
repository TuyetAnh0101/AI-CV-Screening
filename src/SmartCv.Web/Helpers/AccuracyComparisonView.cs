using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

public static class AccuracyComparisonView
{
    public static bool Valid(PromptTestRunDto r) => r.Status == "Completed" && r.CompletedAt is not null
        && r.AccuracyPercent is >= 0 and <= 100 && r.TotalLabeledCount is > 0
        && r.CorrectCount is >= 0 && r.MissedCount is >= 0 && r.WrongPickCount is >= 0 && r.UnscoredCount is >= 0
        && r.KeywordOnlyCount is >= 0 && r.KeywordOnlyCount <= r.TotalLabeledCount
        && (long)r.CorrectCount.Value + r.MissedCount.Value + r.WrongPickCount.Value + r.UnscoredCount.Value == r.TotalLabeledCount
        && decimal.Round(r.CorrectCount.Value * 100m / r.TotalLabeledCount.Value, 2, MidpointRounding.AwayFromZero) == r.AccuracyPercent;

    public static List<PromptTestRunDto> Latest(IEnumerable<PromptTestRunDto> runs, int setId) => runs
        .Where(r => r.EvaluationSetId == setId && Valid(r)).GroupBy(r => r.PromptVersionId)
        .Select(g => g.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.RunId).First())
        .OrderBy(r => r.PromptVersionId).ToList();

    public static PromptTestRunDto? Best(IReadOnlyList<PromptTestRunDto> rows, bool verified) =>
        !verified || rows.Count < 2 || rows.Any(r => !Valid(r) || r.UnscoredCount != 0 || r.KeywordOnlyCount != 0)
            ? null : rows.OrderByDescending(r => r.AccuracyPercent).ThenBy(r => r.MissedCount)
                .ThenBy(r => r.PromptVersionId).ThenBy(r => r.RunId).First();

    public static bool Matches(PromptTestRunDto row, PromptTestSummaryDto snapshot) => row.RunId == snapshot.RunId
        && row.EvaluationSetId == snapshot.EvaluationSetId && row.PromptVersionId == snapshot.PromptVersionId
        && row.AccuracyPercent == snapshot.AccuracyPercent && row.CorrectCount == snapshot.CorrectCount
        && row.MissedCount == snapshot.MissedCount && row.WrongPickCount == snapshot.WrongPickCount;
}
