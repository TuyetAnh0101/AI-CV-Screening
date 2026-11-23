extern alias web;

using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests;

public sealed class UiRefinementTests
{
    [Fact]
    public void Counts_independent_filter_conditions_without_affecting_results()
    {
        Assert.Equal(0, RankingFilterDisplay.ActiveCount(false, false, false, false, false));
        Assert.Equal(1, RankingFilterDisplay.ActiveCount(true, false, false, false, false));
        Assert.Equal(3, RankingFilterDisplay.ActiveCount(true, true, true, false, false));
        Assert.Equal(5, RankingFilterDisplay.ActiveCount(true, true, true, true, true));
    }

    [Fact]
    public void Visible_count_never_invents_missing_or_extra_applications()
    {
        Assert.Equal("3/3 hồ sơ", RankingFilterDisplay.CountLabel(3, 3));
        Assert.Equal("0/3 hồ sơ", RankingFilterDisplay.CountLabel(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => RankingFilterDisplay.CountLabel(4, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => RankingFilterDisplay.CountLabel(-1, 3));
    }
}
