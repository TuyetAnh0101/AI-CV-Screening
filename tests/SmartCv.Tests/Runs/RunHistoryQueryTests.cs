using SmartCv.Core.Contracts;
using SmartCv.Core.Runs;

namespace SmartCv.Tests.Runs;

public class RunHistoryQueryTests
{
    [Fact]
    public void Default_and_third_page_keep_legacy_mode_separate_from_paging()
    {
        var (legacy, error) = RunHistoryQuery.Create(null, null, null, null, null, null, null);
        Assert.Null(error); Assert.False(legacy!.IsPaged); Assert.Equal((1, 20, 0, 20), (legacy.Page, legacy.PageSize, legacy.Skip, legacy.Take));
        Assert.Null(legacy.FromUtc); Assert.Null(legacy.ToUtc);
        var (third, _) = RunHistoryQuery.Create("Screening", "Completed", null, null, 1, 3, 20);
        Assert.True(third!.IsPaged); Assert.Equal(40, third.Skip);
    }

    [Theory]
    [InlineData("type", "screening")]
    [InlineData("type", "Other")]
    [InlineData("type", "")]
    [InlineData("status", "Done")]
    [InlineData("status", "completed")]
    [InlineData("page", "0")]
    [InlineData("page", "-1")]
    [InlineData("page", "abc")]
    [InlineData("page", "2147483648")]
    [InlineData("page", "1.5")]
    [InlineData("pageSize", "0")]
    [InlineData("pageSize", "101")]
    [InlineData("jobId", "0")]
    [InlineData("jobId", "-1")]
    [InlineData("take", "1e3")]
    [InlineData("from", "2026-02-29")]
    [InlineData("to", "2026-1-01")]
    [InlineData("from", "2026-11-17T00:00:00Z")]
    [InlineData("from", "0001-01-01")]
    [InlineData("to", "9999-12-31")]
    public void Invalid_values_produce_field_errors_without_throwing(string field, string value)
    {
        var fields = new Dictionary<string, string[]> { ["page"] = ["1"], [field] = [value] };
        var (query, errors) = RunHistoryQuery.Parse(fields);
        Assert.Null(query); Assert.Contains(field, errors);
    }

    [Fact]
    public void Repeated_keys_blank_values_pageSize_without_page_and_offset_overflow_are_rejected()
    {
        foreach (var field in new[] { "type", "status", "from", "to", "jobId", "take", "page", "pageSize" })
        {
            Assert.Contains(field, RunHistoryQuery.Parse(new Dictionary<string, string[]> { [field] = ["1", "2"] }).Errors);
            Assert.Contains(field, RunHistoryQuery.Parse(new Dictionary<string, string[]> { [field] = [" "] }).Errors);
        }
        Assert.Contains("pageSize", RunHistoryQuery.Parse(new Dictionary<string, string[]> { ["pageSize"] = ["20"] }).Errors);
        Assert.Contains("page", RunHistoryQuery.Parse(new Dictionary<string, string[]> { ["page"] = [int.MaxValue.ToString()], ["pageSize"] = ["100"] }).Errors);
        var (large, errors) = RunHistoryQuery.Parse(new Dictionary<string, string[]> { ["page"] = [int.MaxValue.ToString()], ["pageSize"] = ["1"] });
        Assert.Empty(errors); Assert.Equal(int.MaxValue - 1, large!.Skip);
    }

    [Theory]
    [InlineData("2026-12-01", "2026-11-30T17:00:00Z", "2026-12-01T17:00:00Z")]
    [InlineData("2024-02-29", "2024-02-28T17:00:00Z", "2024-02-29T17:00:00Z")]
    public void Vietnamese_days_include_midnight_and_exclude_next_midnight(string day, string start, string end)
    {
        var (query, errors) = RunHistoryQuery.Parse(new Dictionary<string, string[]> { ["from"] = [day], ["to"] = [day] });
        Assert.Empty(errors); Assert.Equal(DateTime.Parse(start).ToUniversalTime(), query!.FromUtc); Assert.Equal(DateTime.Parse(end).ToUniversalTime(), query.ToUtc);
        Assert.Equal(DateTimeKind.Utc, query.FromUtc!.Value.Kind);
    }

    [Fact]
    public void Reverse_dates_and_unrepresentable_extremes_fail_but_representable_extremes_work()
    {
        Assert.Null(RunHistoryQuery.Create(null, null, new(2026, 11, 18), new(2026, 11, 17), null, 1, null).Query);
        Assert.Null(RunHistoryQuery.Create(null, null, DateOnly.MinValue, null, null, 1, null).Query);
        Assert.Null(RunHistoryQuery.Create(null, null, null, DateOnly.MaxValue, null, 1, null).Query);
        Assert.NotNull(RunHistoryQuery.Create(null, null, DateOnly.MaxValue, null, null, 1, null).Query);
        Assert.NotNull(RunHistoryQuery.Create(null, null, null, DateOnly.MinValue, null, 1, null).Query);
    }

    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    [InlineData(2147483647, 100, 21474837)]
    public void Total_pages_does_not_overflow(int count, int size, int expected) => Assert.Equal(expected, RunHistoryQuery.TotalPages(count, size));

    [Fact]
    public void Duration_and_nullable_metadata_are_safe_and_record_with_expressions_remain_compatible()
    {
        var start = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(RunHistoryQuery.DurationSeconds(start, null));
        Assert.Equal(0, RunHistoryQuery.DurationSeconds(start, start.AddSeconds(-1)));
        Assert.Equal(12, RunHistoryQuery.DurationSeconds(start, start.AddMilliseconds(12999)));
        Assert.Equal(int.MaxValue, RunHistoryQuery.DurationSeconds(DateTime.MinValue, DateTime.MaxValue));
        var dto = new RunProgressDto(1, "Screening", "Queued", 1, "v1", "model", .2m, .3m, .7m, 70, 1, 0, 0, 0, start, null);
        var copy = dto with { Percent = 50, SystemPrompt = "saved" };
        Assert.Equal(50, copy.Percent); Assert.Null(copy.EvaluationSetId); Assert.Null(copy.EvaluationSetName); Assert.Null(copy.AccuracyPercent); Assert.Empty(dto.SystemPrompt);
    }

    [Theory]
    [InlineData("-1", 1)]
    [InlineData("0", 1)]
    [InlineData("3", 3)]
    [InlineData("500", 100)]
    public void Legacy_take_clamps_as_before(string take, int expected) => Assert.Equal(expected, RunHistoryQuery.Parse(new Dictionary<string, string[]> { ["take"] = [take] }).Query!.Take);
}
