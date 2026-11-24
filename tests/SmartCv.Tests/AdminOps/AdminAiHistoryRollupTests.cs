using SmartCv.Api.Ops;

namespace SmartCv.Tests.AdminOps;

public sealed class AdminAiHistoryRollupTests
{
    private static readonly DateOnly Today = new(2026, 11, 23);
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static AdminRunHistoryGroup R(string model, string type, string status, long n) =>
        new(model, type, status, n, Now.AddDays(-4), Now);
    private static AdminEvaluationHistoryGroup E(string model, string type, string status, string source, long n, long ai) =>
        new(new DateOnly(2026, 11, 20), model, type, status, source, n, ai);

    [Fact]
    public void Historical_sql_snapshot_gives_exact_22_79_46_26_7_without_inventing_quota_or_tokens()
    {
        var runs = new[] {
            R("gemini-2.5-flash", "Screening", "Completed", 8),
            R("gemini-2.5-flash", "PromptTest", "Completed", 2),
            R("gemini-3.1-flash-lite", "Screening", "Completed", 2),
            R("gemini-3.1-flash-lite", "PromptTest", "Completed", 1),
            R("gemini-3.8-flash", "Screening", "Completed", 5),
            R("gemini-3.8-flash", "Screening", "Failed", 4)
        };
        var groups = new[] {
            E("gemini-2.5-flash", "PromptTest", "Completed", "Combined", 20, 20),
            E("gemini-2.5-flash", "Screening", "Completed", "Combined", 12, 12),
            E("gemini-2.5-flash", "Screening", "Completed", "KeywordOnly", 16, 0),
            E("gemini-2.5-flash", "Screening", "Failed", "Unknown", 1, 0),
            E("gemini-3.1-flash-lite", "PromptTest", "Completed", "Combined", 10, 10),
            E("gemini-3.1-flash-lite", "Screening", "Completed", "Combined", 4, 4),
            E("gemini-3.8-flash", "Screening", "Completed", "KeywordOnly", 10, 0),
            E("gemini-3.8-flash", "Screening", "Failed", "Unknown", 6, 0)
        };
        var x = AdminAiHistoryRollup.Build(Now, "all", Today, runs, groups);
        Assert.Equal(22, x.TotalRuns);
        Assert.Equal(18, x.CompletedRuns);
        Assert.Equal(4, x.FailedRuns);
        Assert.Equal(79, x.TotalEvaluations);
        Assert.Equal(46, x.CombinedEvaluations);
        Assert.Equal(26, x.KeywordOnlyEvaluations);
        Assert.Equal(7, x.FailedEvaluations);
        Assert.Equal(46, x.EvaluationsWithAiScore);
        Assert.Equal(0, x.OtherEvaluations);
        Assert.Equal("month", x.ChartBucket);
        Assert.Null(x.FromDate);
        Assert.Equal(3, x.Models.Count);
        var m38 = Assert.Single(x.Models, m => m.Model == "gemini-3.8-flash");
        Assert.Equal((9L, 16L, 0L, 10L, 6L),
            (m38.Runs, m38.Evaluations, m38.Combined, m38.KeywordOnly, m38.Failed));
        Assert.Equal((46L,26L,7L),
            (x.Trend.Sum(p => p.Combined), x.Trend.Sum(p => p.KeywordOnly), x.Trend.Sum(p => p.Failed)));
    }

    [Fact]
    public void Fixed_window_fills_days_but_never_invents_events()
    {
        var x = AdminAiHistoryRollup.Build(Now, "7", Today, [],
            [E("m", "Screening", "Completed", "KeywordOnly", 2, 0)]);
        Assert.Equal(7, x.Trend.Count);
        Assert.Equal(new DateOnly(2026,11,17), x.FromDate);
        Assert.Equal(2, x.KeywordOnlyEvaluations);
        Assert.Equal(1, x.Trend.Count(p => p.KeywordOnly == 2));
        Assert.All(x.Trend.Where(p => p.Bucket != new DateOnly(2026,11,20)), p => Assert.Equal(0, p.Total));
    }

    [Fact]
    public void Finished_run_with_failed_evaluations_is_not_reclassified_as_failed_run()
    {
        var x = AdminAiHistoryRollup.Build(Now, "14", Today,
            [R("m", "Screening", "Completed", 1)],
            [E("m", "Screening", "Failed", "Unknown", 1, 0)]);
        Assert.Equal(1, x.CompletedRuns);
        Assert.Equal(0, x.FailedRuns);
        Assert.Equal(1, x.FailedEvaluations);
    }

    [Fact]
    public void Pending_and_unknown_sources_do_not_count_as_combined_or_keyword()
    {
        var x = AdminAiHistoryRollup.Build(Now, "14", Today,
            [R("m", "Screening", "Running", 1)],
            [E("m", "Screening", "Pending", "Unknown", 2, 0),
             E("m", "Screening", "Completed", "Unexpected", 1, 0)]);
        Assert.Equal(3, x.OtherEvaluations);
        Assert.Equal(1, x.ActiveRuns);
        Assert.Equal(0, x.CombinedEvaluations);
    }

    [Fact]
    public void Invalid_period_or_impossible_token_count_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdminAiHistoryRollup.Build(Now, "3650", Today, [], []));
        Assert.Throws<ArgumentException>(() => AdminAiHistoryRollup.Build(Now, "7", Today, [],
            [E("m", "Screening", "Completed", "Combined", 1, 2)]));
    }
}
