using SmartCv.Core.Contracts;
using SmartCv.Core.Jobs;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;

namespace SmartCv.Tests.Jobs;

public class JobSkillRulesTests
{
    [Theory]
    [InlineData("C#", "CSHARP")]
    [InlineData("C++", "CPLUSPLUS")]
    [InlineData("ASP.NET Core", "ASPNET_CORE")]
    [InlineData("Kiểm thử tự động", "KIEM_THU_TU_DONG")]
    [InlineData("  SQL Server  ", "SQL_SERVER")]
    [InlineData("Đọc Đặc tả", "DOC_DAC_TA")]
    [InlineData("Kiểm thử", "KIEM_THU")]
    [InlineData("...😀...", "")]
    public void Suggests_ASCII_codes_from_names(string name, string expected) => Assert.Equal(expected, JobSkillRules.SuggestCode(name));

    [Theory]
    [InlineData(" csharp ", "CSHARP")]
    [InlineData("kiểm thử", "KIEM_THU")]
    [InlineData("__ASP--NET___CORE__", "ASP_NET_CORE")]
    public void Normalizes_client_codes(string code, string expected) => Assert.Equal(expected, JobSkillRules.NormalizeCode(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("_CSHARP")]
    [InlineData("c sharp")]
    [InlineData("CSHARP\n")]
    [InlineData("ĐOC")]
    public void Rejects_invalid_canonical_codes(string? code) => Assert.False(JobSkillRules.IsValidCode(code));

    [Fact]
    public void Code_limits_and_long_suggestion_are_enforced()
    {
        Assert.True(JobSkillRules.IsValidCode("A1")); Assert.True(JobSkillRules.IsValidCode(new string('A', 50)));
        Assert.False(JobSkillRules.IsValidCode(new string('A', 51)));
        Assert.Equal(50, JobSkillRules.SuggestCode(new string('Đ', 100)).Length);
        Assert.True(JobSkillRules.IsValidCode(JobSkillRules.SuggestCode(new string('A', 49) + " test")));
    }

    [Fact]
    public void Aliases_trim_collapse_lowercase_deduplicate_and_compose_Unicode()
    {
        var (aliases, error) = JobSkillRules.NormalizeAliases([null, " ", "  Dot\t NET ", "dot net", "Kiểm thử", "Kiểm thử"]);
        Assert.Null(error); Assert.Equal(["dot net", "kiểm thử"], aliases);
        Assert.Empty(JobSkillRules.NormalizeAliases(null).Aliases);
    }

    [Fact]
    public void Alias_limits_apply_after_normalization()
    {
        var thirty = Enumerable.Range(0, 30).Select(i => (string?)$"keyword {i}").ToList();
        Assert.Null(JobSkillRules.NormalizeAliases(thirty.Concat(thirty)).Error);
        Assert.NotNull(JobSkillRules.NormalizeAliases(thirty.Append("31st")).Error);
        Assert.Null(JobSkillRules.NormalizeAliases([new string('x', 100)]).Error);
        Assert.NotNull(JobSkillRules.NormalizeAliases([new string('x', 101)]).Error);
    }

    [Theory]
    [InlineData(null, "MustHave", "displayName")]
    [InlineData(" ", "MustHave", "displayName")]
    [InlineData("C#", null, "skillType")]
    [InlineData("C#", "Required", "skillType")]
    [InlineData("C#", "musthave", "skillType")]
    public void Validates_display_and_exact_skill_type(string? name, string? type, string field) => Assert.Contains(field, JobSkillRules.Validate(name, type));

    [Fact]
    public void ValidateNew_handles_null_invalid_and_default_suggested_codes()
    {
        Assert.Null(JobSkillRules.ValidateNew(null).Normalized);
        var (normalized, errors) = JobSkillRules.ValidateNew(new() { DisplayName = "  C#  ", SkillType = "NiceToHave", Aliases = [" DOT NET "] });
        Assert.Empty(errors); Assert.Equal("CSHARP", normalized!.SkillCode); Assert.Equal("C#", normalized.DisplayName); Assert.Equal(["dot net"], normalized.Aliases);
        Assert.Contains("skillCode", JobSkillRules.ValidateNew(new() { DisplayName = "...", SkillType = "MustHave" }).Errors);
        Assert.Contains("skillCode", JobSkillRules.ValidateNew(new() { SkillCode = new string('A', 51), DisplayName = "C#", SkillType = "MustHave" }).Errors);
        Assert.Contains("displayName", JobSkillRules.Validate(new string('x', 101), "MustHave"));
        Assert.Empty(JobSkillRules.Validate(new string('x', 100), "NiceToHave"));
    }

    [Fact]
    public void New_alias_changes_real_keyword_engine_score_and_display_name_still_matches()
    {
        var profile = new JobProfile(1, "Test", null, null, 0, [new("ASPNET_CORE", "ASP.NET Core", "MustHave", [])]);
        const string cv = "Kinh nghiệm 2 năm lập trình dot net và kiểm thử.";
        var before = KeywordScoringService.Score(profile, cv, new ScoringOptions(), new(2026, 11, 17));
        var after = KeywordScoringService.Score(profile with { Skills = [profile.Skills[0] with { Aliases = ["dot net"] }] }, cv, new ScoringOptions(), new(2026, 11, 17));
        Assert.False(before.Skills.Single().IsMatched); Assert.True(after.Skills.Single().IsMatched); Assert.True(after.Score > before.Score);
        var displayMatch = KeywordScoringService.Score(profile, "Có ASP.NET Core", new ScoringOptions(), new(2026, 11, 17));
        Assert.True(displayMatch.Skills.Single().IsMatched);
    }
}
