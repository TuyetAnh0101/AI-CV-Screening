using SmartCv.Core.Common;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Dashboard;

namespace SmartCv.Tests.Dashboard;

public class DashboardCalculatorTests
{
    [Theory]
    [InlineData("2026-11-17", "2026-11-17", "2026-11-16T17:00:00Z", "2026-11-17T17:00:00Z")]
    [InlineData("2024-02-29", "2024-03-01", "2024-02-28T17:00:00Z", "2024-03-01T17:00:00Z")]
    [InlineData("2026-12-31", "2027-01-01", "2026-12-30T17:00:00Z", "2027-01-01T17:00:00Z")]
    public void Vietnam_dates_include_both_days_with_exclusive_upper_bound(string from, string to, string start, string end)
    {
        var (period, error) = DashboardPeriod.Parse(from, to);
        Assert.Null(error);
        Assert.Equal(DateTime.Parse(start).ToUniversalTime(), period!.FromUtc);
        Assert.Equal(DateTime.Parse(end).ToUniversalTime(), period.ToUtc);
        Assert.Equal(DateTimeKind.Utc, period.FromUtc!.Value.Kind);
        Assert.True(period.Contains(period.FromUtc.Value));
        Assert.False(period.Contains(period.FromUtc.Value.AddTicks(-1)));
        Assert.True(period.Contains(period.ToUtc!.Value.AddTicks(-1)));
        Assert.False(period.Contains(period.ToUtc.Value));
    }

    [Theory]
    [InlineData("2026-11-18", "2026-11-17")]
    [InlineData("2026-02-29", null)]
    [InlineData("2026-13-01", null)]
    [InlineData("2026-2-01", null)]
    [InlineData(" 2026-11-17", null)]
    [InlineData("2026-11-17T00:00:00Z", null)]
    [InlineData("0001-01-01", null)]
    [InlineData(null, "9999-12-31")]
    public void Invalid_or_unrepresentable_dates_return_error(string? from, string? to)
    {
        var (period, error) = DashboardPeriod.Parse(from, to);
        Assert.Null(period);
        Assert.NotNull(error);
    }

    [Fact]
    public void Open_bounds_and_empty_filters_are_unlimited()
    {
        Assert.Null(DashboardPeriod.Parse(null, "").Period!.FromUtc);
        Assert.Null(DashboardPeriod.Parse(null, "").Period!.ToUtc);
        Assert.Null(DashboardPeriod.Parse("2026-11-17", null).Period!.ToUtc);
        Assert.Null(DashboardPeriod.Parse(null, "2026-11-17").Period!.FromUtc);
        Assert.Equal(new DateOnly(2026, 11, 18), VietnamTime.Today(new DateTime(2026, 11, 17, 17, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Funnel_has_four_stable_steps_and_zero_denominators()
    {
        var empty = FunnelCalculator.Build(new Dictionary<string, int>(), [0, 0, 0, 0]);
        Assert.Equal(FunnelCalculator.MainStages, empty.Select(s => s.Stage));
        Assert.Null(empty[0].ConversionPercent);
        Assert.All(empty.Skip(1), s => Assert.Equal(0, s.ConversionPercent));
        var funnel = FunnelCalculator.Build(new Dictionary<string, int> { [ApplicationStages.New] = 2 }, [5, 3, 2, 1]);
        Assert.Equal(new decimal?[] { null, 60, 66.7m, 50 }, funnel.Select(s => s.ConversionPercent));
        Assert.Equal(2, funnel[0].Current);
    }

    [Theory]
    [InlineData(new double[] { 2, 4, 48 }, 18, 4)]
    [InlineData(new double[] { 1, 2, 3, 10 }, 4, 2.5)]
    [InlineData(new double[] { -1, 2 }, 1, 1)]
    [InlineData(new double[] { 0.05 }, 0.1, 0.1)]
    public void Screening_handles_odd_even_outlier_negative_and_rounding(double[] hours, double average, double median)
    {
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = ScreeningTimeCalculator.Compute(hours.Select(h => (start, start.AddHours(h))));
        Assert.Equal(hours.Length, result.Count);
        Assert.Equal((decimal)average, result.AverageHours);
        Assert.Equal((decimal)median, result.MedianHours);
    }

    [Fact]
    public void No_decisions_have_null_average_and_median()
    {
        var result = ScreeningTimeCalculator.Compute([]);
        Assert.Equal(0, result.Count);
        Assert.Null(result.AverageHours);
        Assert.Null(result.MedianHours);
    }

    [Fact]
    public void Reasons_use_all_rejected_as_denominator_including_missing_legacy_reason()
    {
        var result = RejectReasonCalculator.Build(new Dictionary<string, int>
        {
            [RejectReasons.MissingSkills] = 1, [RejectReasons.Duplicate] = 1
        }, 3);
        Assert.Equal(6, result.Count);
        Assert.Equal(RejectReasons.MissingSkills, result[0].Reason);
        Assert.Equal(33.3m, result[0].Percent);
        Assert.True(result[0].IsProfessional);
        Assert.False(result[1].IsProfessional);
        Assert.Equal(66.6m, result.Sum(r => r.Percent));
        Assert.Equal(RejectReasons.All, RejectReasonCalculator.Build(new Dictionary<string, int>(), 0).Select(r => r.Reason));
        Assert.True(new DashboardSummaryDto().IsEmpty);
        Assert.False(new DashboardSummaryDto { TotalCvs = 1 }.IsEmpty);
        Assert.False(new DashboardSummaryDto { TotalApplications = 1 }.IsEmpty);
    }
}
