using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Core.Dashboard;

public static class FunnelCalculator
{
    public static IReadOnlyList<string> MainStages { get; } = Array.AsReadOnly(new[]
        { ApplicationStages.New, ApplicationStages.Reviewing, ApplicationStages.Shortlisted, ApplicationStages.Interview });

    /// <summary>Inputs are SQL counts, never a materialized collection of application history.</summary>
    public static List<FunnelStepDto> Build(IReadOnlyDictionary<string, int> current, IReadOnlyList<int> reached) =>
        MainStages.Select((stage, i) => new FunnelStepDto
        {
            Stage = stage,
            StageName = ApplicationStages.DisplayName(stage),
            Current = current.GetValueOrDefault(stage),
            Reached = reached[i],
            ConversionPercent = i == 0 ? null : reached[i - 1] == 0 ? 0m
                : Math.Round(reached[i] * 100m / reached[i - 1], 1, MidpointRounding.AwayFromZero)
        }).ToList();
}
