using SmartCv.Core.Common;
using SmartCv.Core.Contracts;
using SmartCv.Core.Jobs;

namespace SmartCv.Tests.Jobs;

public class JobRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n\u00a0 ")]
    public void Empty_titles_have_field_errors(string? title) =>
        Assert.Contains("title", JobRules.Validate(new() { Title = title }, Today, true).Keys);

    [Fact]
    public void Title_normalization_collapses_all_whitespace_preserves_case_and_enforces_normalized_length()
    {
        Assert.Equal("Backend Developer (.NET)", JobRules.NormalizeTitle(" \tBackend\n Developer\u00a0 (.NET)\r "));
        Assert.Empty(JobRules.Validate(new() { Title = "  " + new string('a', 150) + "  " }, Today, true));
        Assert.Contains("title", JobRules.Validate(new() { Title = new string('a', 151) }, Today, true).Keys);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("0", true)]
    [InlineData("4", true)]
    [InlineData("-0.01", false)]
    [InlineData("4.01", false)]
    public void Gpa_boundaries(string? value, bool valid)
    {
        var request = Valid();
        request.MinGpa = value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(valid, !JobRules.Validate(request, Today, true).ContainsKey("minGpa"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(50, true)]
    [InlineData(-1, false)]
    [InlineData(51, false)]
    public void Experience_boundaries(int years, bool valid)
    {
        var request = Valid(); request.MinYearsExperience = years;
        Assert.Equal(valid, !JobRules.Validate(request, Today, true).ContainsKey("minYearsExperience"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(1000, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1001, false)]
    public void Headcount_boundaries(int? value, bool valid)
    {
        var request = Valid(); request.Headcount = value;
        Assert.Equal(valid, !JobRules.Validate(request, Today, true).ContainsKey("headcount"));
    }

    [Fact]
    public void Description_limit_and_multiple_errors_use_camelCase_fields()
    {
        var request = Valid(); request.Description = new string('a', 20_000);
        Assert.Empty(JobRules.Validate(request, Today, true));
        request.Title = " "; request.Description += "a"; request.MinGpa = 5; request.MinYearsExperience = -1;
        request.Headcount = 0; request.ClosingDate = Today.AddDays(-1);
        Assert.Equal(new[] { "title", "description", "minGpa", "minYearsExperience", "headcount", "closingDate" }, JobRules.Validate(request, Today, true).Keys);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Closing_date_is_inclusive_today_for_create_and_past_is_allowed_on_edit(int offset, bool validCreate)
    {
        var request = Valid(); request.ClosingDate = Today.AddDays(offset);
        Assert.Equal(validCreate, !JobRules.Validate(request, Today, true).ContainsKey("closingDate"));
        Assert.Empty(JobRules.Validate(request, Today, false));
        Assert.Equal(offset < 0, JobRules.IsExpired(request.ClosingDate, Today));
        Assert.False(JobRules.IsExpired(null, Today));
    }

    [Fact]
    public void Vietnam_day_changes_at_17_UTC_and_handles_leap_and_year_boundaries()
    {
        var before = new DateTime(2026, 11, 17, 16, 59, 59, DateTimeKind.Utc);
        Assert.Equal(Today, VietnamTime.Today(before));
        Assert.Equal(Today.AddDays(1), VietnamTime.Today(before.AddSeconds(1)));
        Assert.Equal(new DateOnly(2024, 2, 29), VietnamTime.Today(new DateTime(2024, 2, 28, 17, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2027, 1, 1), VietnamTime.Today(new DateTime(2026, 12, 31, 17, 0, 0, DateTimeKind.Utc)));
    }

    internal static JobUpsertRequest Valid(string title = "Vị trí A21") => new()
    {
        Title = title, Description = "Mô tả tổng hợp", MinGpa = 2.5m, MinYearsExperience = 1,
        Headcount = 2, ClosingDate = Today
    };
}
