using SmartCv.Core.Contracts;

namespace SmartCv.Core.Dashboard;

public static class ScreeningTimeCalculator
{
    public static ScreeningTimeDto Compute(IEnumerable<(DateTime Start, DateTime FirstDecision)> items)
    {
        var hours = items.Select(x => Math.Max(0m, (x.FirstDecision.Ticks - x.Start.Ticks) / (decimal)TimeSpan.TicksPerHour))
            .Order().ToArray();
        if (hours.Length == 0) return new();
        var mid = hours.Length / 2;
        return FromAggregate(hours.Length, hours.Average(), hours.Length % 2 == 1 ? hours[mid] : (hours[mid - 1] + hours[mid]) / 2);
    }

    public static ScreeningTimeDto FromAggregate(int count, decimal? average, decimal? median) => new()
    {
        Count = count,
        AverageHours = count == 0 ? null : Round(average),
        MedianHours = count == 0 ? null : Round(median)
    };

    private static decimal? Round(decimal? value) => value is { } v ? Math.Round(v, 1, MidpointRounding.AwayFromZero) : null;
}
