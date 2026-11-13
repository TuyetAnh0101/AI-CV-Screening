using SmartCv.Core.Scoring;

namespace SmartCv.Tests;

/// <summary>A42: ước tính thời gian chấm từ các lượt Screening cũ. Hàm thuần, không database.</summary>
public class RunTimeEstimatorTests
{
    private static RunDurationSample S(int items, double seconds) => new(items, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void No_history_uses_gemini_spacing_plus_two_seconds()
    {
        var e = RunTimeEstimator.Estimate(10, [], minDelayBetweenCallsMs: 4500);

        Assert.Equal((6.5m, 65, 2, 0), (e.SecondsPerCv, e.EstimatedSeconds, e.EstimatedMinutes, e.BasedOnRuns));
        Assert.True(e.IsDefault);
        Assert.Equal(4.5m, e.MinSecondsPerCv);
        Assert.Equal("10 CV · khoảng 2 phút", e.Text);
    }

    [Theory]
    [InlineData(4500, 6.5)]    // Gemini thật (mặc định appsettings)
    [InlineData(800, 2.8)]     // AI giả: FakeDelayMs mặc định
    [InlineData(0, 2.0)]       // AI giả không chờ
    public void Default_follows_the_configured_floor(int floorMs, double expected)
    {
        Assert.Equal((decimal)expected, RunTimeEstimator.Estimate(1, [], floorMs).SecondsPerCv);
    }

    [Fact]
    public void Average_is_total_time_over_total_cvs_not_mean_of_runs()
    {
        // 10 CV hết 100 giây + 30 CV hết 180 giây → 280 / 40 = 7 giây/CV (trung bình theo lượt sẽ là 8)
        var e = RunTimeEstimator.Estimate(20, [S(10, 100), S(30, 180)], 4500);

        Assert.Equal((7m, 140, 3, 2), (e.SecondsPerCv, e.EstimatedSeconds, e.EstimatedMinutes, e.BasedOnRuns));
        Assert.False(e.IsDefault);
    }

    [Fact]
    public void Never_faster_than_the_gemini_spacing()
    {
        Assert.Equal(4.5m, RunTimeEstimator.Estimate(10, [S(10, 5)], 4500).SecondsPerCv);
    }

    [Fact]
    public void Invalid_and_abnormal_samples_are_ignored()
    {
        var e = RunTimeEstimator.Estimate(5,
            [S(0, 50), S(5, 0), S(5, -10), S(1, 3_601), S(2, 20)], 4500);   // chỉ mẫu cuối hợp lệ: 10 giây/CV

        Assert.Equal((10m, 1), (e.SecondsPerCv, e.BasedOnRuns));
    }

    [Fact]
    public void Only_the_first_ten_valid_samples_are_used()
    {
        // Người gọi đưa mẫu mới nhất trước: 10 mẫu 8 giây/CV rồi 5 mẫu rất chậm
        var samples = Enumerable.Repeat(S(10, 80), 10).Concat(Enumerable.Repeat(S(1, 1_000), 5));

        var e = RunTimeEstimator.Estimate(10, samples, 4500);

        Assert.Equal((8m, 10), (e.SecondsPerCv, e.BasedOnRuns));
    }

    [Fact]
    public void Seconds_per_cv_is_rounded_to_one_decimal_and_totals_round_up()
    {
        // 100 giây / 15 CV = 6,666… → 6,7; 7 CV → 46,9 giây → 47 giây, 1 phút
        var e = RunTimeEstimator.Estimate(7, [S(15, 100)], 4500);

        Assert.Equal((6.7m, 47, 1), (e.SecondsPerCv, e.EstimatedSeconds, e.EstimatedMinutes));
        Assert.Equal("7 CV · dưới 1 phút", e.Text);
    }

    [Fact]
    public void Zero_cvs_is_zero_time()
    {
        var e = RunTimeEstimator.Estimate(0, [S(10, 80)], 4500);

        Assert.Equal((0, 0, "0 CV"), (e.EstimatedSeconds, e.EstimatedMinutes, e.Text));
    }

    [Fact]
    public void Largest_count_with_slowest_allowed_sample_does_not_overflow()
    {
        var e = RunTimeEstimator.Estimate(RunTimeEstimator.MaxCount, [S(1, 3_600)], 600_000);

        Assert.Equal(3_600m, e.SecondsPerCv);
        Assert.Equal(1_800_000, e.EstimatedSeconds);
        Assert.Equal("500 CV · khoảng 500 giờ", e.Text);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(501)]
    public void Count_outside_0_to_500_is_rejected(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RunTimeEstimator.Estimate(count, [], 4500));
    }

    [Theory]
    [InlineData(3, 20, "3 CV · dưới 1 phút")]
    [InlineData(10, 59, "10 CV · dưới 1 phút")]
    [InlineData(10, 60, "10 CV · khoảng 1 phút")]
    [InlineData(10, 65, "10 CV · khoảng 2 phút")]
    [InlineData(200, 3_600, "200 CV · khoảng 1 giờ")]
    [InlineData(200, 4_500, "200 CV · khoảng 1 giờ 15 phút")]
    [InlineData(0, 0, "0 CV")]
    public void Describe_is_friendly_vietnamese(int count, int seconds, string expected)
    {
        Assert.Equal(expected, RunTimeEstimator.Describe(count, seconds));
    }
}
