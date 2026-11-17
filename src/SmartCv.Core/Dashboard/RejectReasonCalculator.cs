using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Evaluation;

namespace SmartCv.Core.Dashboard;

public static class RejectReasonCalculator
{
    /// <summary>Missing legacy reasons stay missing; the denominator includes every rejected application.</summary>
    public static List<ReasonCountDto> Build(IReadOnlyDictionary<string, int> counts, int rejectedTotal) =>
        RejectReasons.All.Select(reason => new ReasonCountDto
        {
            Reason = reason,
            ReasonName = RejectReasons.DisplayName(reason),
            IsProfessional = LabelSuggestionRules.Suggest(ApplicationStages.Rejected, reason) == false,
            Count = counts.GetValueOrDefault(reason),
            Percent = rejectedTotal == 0 ? 0m : Math.Round(counts.GetValueOrDefault(reason) * 100m / rejectedTotal, 1, MidpointRounding.AwayFromZero)
        }).OrderByDescending(r => r.Count).ThenBy(r => RejectReasons.All.IndexOf(r.Reason)).ToList();

    private static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++) if (values[i] == value) return i;
        return int.MaxValue;
    }
}
