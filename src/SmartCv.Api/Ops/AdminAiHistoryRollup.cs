using SmartCv.Core.Contracts;

namespace SmartCv.Api.Ops;

/// <summary>Input is already aggregated SQL data. No CV, applicant, prompt or provider payload is read.</summary>
public sealed record AdminRunHistoryGroup(
    string Model, string RunType, string Status, long Count,
    DateTime? FirstUtc, DateTime? LastUtc);

public sealed record AdminEvaluationHistoryGroup(
    DateOnly Bucket, string Model, string RunType, string Status,
    string ScoreSource, long Count, long WithAiScore);

/// <summary>
/// Classifies stored SCApp outcomes, NOT provider calls. Run Completed means processing
/// terminated; individual evaluations can still be Failed or KeywordOnly.
/// This distinction is enforced by the classification order below.
/// </summary>
public static class AdminAiHistoryRollup
{
    private sealed record Category(long Combined, long KeywordOnly, long Failed, long Other);

    private static Category Classify(AdminEvaluationHistoryGroup e) =>
        e.Status == "Failed" ? new(0, 0, e.Count, 0) :
        e.Status == "Completed" && e.ScoreSource == "Combined" ? new(e.Count, 0, 0, 0) :
        e.Status == "Completed" && e.ScoreSource == "KeywordOnly" ? new(0, e.Count, 0, 0) :
        new(0, 0, 0, e.Count);

    public static AdminAiHistoryDto Build(DateTime capturedAtUtc, string period, DateOnly today,
        IReadOnlyList<AdminRunHistoryGroup> runs,
        IReadOnlyList<AdminEvaluationHistoryGroup> evaluations)
    {
        if (period is not ("7" or "14" or "30" or "90" or "all"))
            throw new ArgumentOutOfRangeException(nameof(period));
        if (runs.Any(r => r.Count < 0) || evaluations.Any(e => e.Count < 0 || e.WithAiScore < 0 || e.WithAiScore > e.Count))
            throw new ArgumentException("Negative or inconsistent aggregate.");
        var totalRuns = runs.Sum(x => x.Count);
        var total = evaluations.Sum(x => x.Count);
        var c = evaluations.Select(Classify).ToArray();
        var modelNames = runs.Select(r => r.Model).Concat(evaluations.Select(e => e.Model))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var models = modelNames.Select(model =>
        {
            var mr = runs.Where(r => string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase)).ToArray();
            var me = evaluations.Where(e => string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase)).ToArray();
            var mc = me.Select(Classify).ToArray();
            return new AdminAiHistoryModelDto(model,
                mr.Sum(r => r.Count), mr.Where(r => r.Status == "Completed").Sum(r => r.Count),
                mr.Where(r => r.Status == "Failed").Sum(r => r.Count),
                me.Sum(e => e.Count), mc.Sum(x => x.Combined), mc.Sum(x => x.KeywordOnly),
                mc.Sum(x => x.Failed), mc.Sum(x => x.Other), me.Sum(x => x.WithAiScore));
        }).OrderByDescending(x => x.Evaluations).ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase).ToArray();

        var byBucket = evaluations.GroupBy(e => e.Bucket).ToDictionary(g => g.Key, g =>
        {
            var gc = g.Select(Classify).ToArray();
            return new AdminAiHistoryPointDto(g.Key, gc.Sum(x => x.Combined),
                gc.Sum(x => x.KeywordOnly), gc.Sum(x => x.Failed), gc.Sum(x => x.Other));
        });
        AdminAiHistoryPointDto[] trend;
        if (period == "all")
        {
            // SQL groups by Vietnam calendar month, rather than returning a row per CV or per day.
            // Historical months without observations are absent, clearly stated on screen.
            trend = byBucket.Values.OrderBy(x => x.Bucket).ToArray();
        }
        else
        {
            var n = int.Parse(period, System.Globalization.CultureInfo.InvariantCulture);
            trend = Enumerable.Range(0, n).Select(i => today.AddDays(i - n + 1))
                .Select(date => byBucket.GetValueOrDefault(date) ?? new AdminAiHistoryPointDto(date, 0, 0, 0, 0))
                .ToArray();
        }

        var first = runs.Where(x => x.FirstUtc.HasValue).Select(x => x.FirstUtc!.Value).DefaultIfEmpty()
            .Min();
        var last = runs.Where(x => x.LastUtc.HasValue).Select(x => x.LastUtc!.Value).DefaultIfEmpty()
            .Max();
        var fromDate = period == "all" ? (DateOnly?)null : today.AddDays(1 - int.Parse(period, System.Globalization.CultureInfo.InvariantCulture));
        return new AdminAiHistoryDto(capturedAtUtc, period, period == "all" ? "month" : "day",
            fromDate, period == "all" ? null : today,
            totalRuns, runs.Where(r => r.Status == "Completed").Sum(r => r.Count),
            runs.Where(r => r.Status == "Failed").Sum(r => r.Count),
            runs.Where(r => r.Status is "Queued" or "Running").Sum(r => r.Count),
            total, c.Sum(x => x.Combined), c.Sum(x => x.KeywordOnly),
            c.Sum(x => x.Failed), c.Sum(x => x.Other),
            evaluations.Sum(x => x.WithAiScore),
            evaluations.Where(x => x.RunType == "Screening").Sum(x => x.Count),
            evaluations.Where(x => x.RunType == "PromptTest").Sum(x => x.Count),
            totalRuns == 0 ? null : first, totalRuns == 0 ? null : last,
            models, trend);
    }
}
