using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

public sealed record FitDistributionSegment(string Label, string Css, string Range, int Count);

/// <summary>B11: one bucket per CV; progress supplies extraction failures missing from A13 ranking.</summary>
public sealed record FitDistributionView(int Total, IReadOnlyList<FitDistributionSegment> Segments, bool Reconciled)
{
    public static FitDistributionView From(RankingResponse ranking, IEnumerable<RunItemDto> progress)
    {
        var items = ranking.Items.DistinctBy(i => i.CvId).ToList();
        var failedIds = progress.Where(p => p.JobId == ranking.JobId && p.ExtractionStatus == ExtractionStatuses.Failed)
            .Select(p => p.EvaluationId).ToHashSet();
        bool Failed(RankingItemDto i) => i.Status == EvaluationStatuses.Failed || failedIds.Contains(i.EvaluationId);
        var scored = items.Where(i => !Failed(i) && RankingView.IsScored(i)).ToList();
        var counts = FitLevel.All.ToDictionary(l => l, l => scored.Count(i => FitLevel.FromScore(i.FinalScore) == l));
        // Use the API aggregate whenever it agrees with the current, unique CV snapshot.
        var useApi = items.Count == ranking.Items.Count && FitLevel.All.All(l =>
            ranking.LevelCounts.TryGetValue(l, out var count) && count == counts[l]);
        var segments = FitLevel.All.Select(l => new FitDistributionSegment(l, FitLevel.CssClass(l),
            FitLevel.Range(l), useApi ? ranking.LevelCounts[l] : counts[l])).ToList();
        var failed = items.Count(Failed);
        var unscored = items.Count - scored.Count - failed;
        if (failed > 0) segments.Add(new("Lỗi xử lý", "is-error", "", failed));
        if (unscored > 0) segments.Add(new(
            ranking.RunStatus is RunStatuses.Queued or RunStatuses.Running ? "Đang chấm" : "Chưa có điểm",
            "is-pending", "", unscored));
        return new(items.Count, segments, !useApi);
    }
}
