using System.Globalization;
using SmartCv.Core.Constants;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;

namespace SmartCv.Tests;

/// <summary>Phân biệt dữ liệu kinh nghiệm chưa biết với dữ liệu xác nhận 0 năm mà không đổi công thức A9.</summary>
public class ExperienceFactRegressionTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static decimal? Years(string text, DateOnly? today = null) =>
        CvFactExtractor.ExtractYearsOfExperience(TextNormalizer.Normalize(text), today ?? Today);

    private static KeywordScoreResult Score(string text, decimal requiredYears = 2m, bool withSkill = true) =>
        KeywordScoringService.Score(
            new JobProfile(1, "Backend Developer", null, null, requiredYears,
                withSkill ? [new SkillProfile("csharp", "C#", SkillTypes.MustHave, [])] : []),
            text, new ScoringOptions(), Today);

    [Theory]
    [InlineData("Thành thạo C#.")]
    [InlineData("Sinh viên ngành công nghệ thông tin.")]
    [InlineData("Sinh viên mới tốt nghiệp, biết C#.")]
    [InlineData("Chưa ghi thông tin kinh nghiệm.")]
    [InlineData("Không yêu cầu kinh nghiệm.")]
    [InlineData("Chưa có kinh nghiệm với Docker.")]
    [InlineData("Chưa có kinh nghiệm SQL.")]
    [InlineData("No experience with Docker.")]
    [InlineData("0 năm kinh nghiệm với Docker.")]
    [InlineData("0 năm kinh nghiệm SQL.")]
    [InlineData("Docker: 0 years of experience.")]
    [InlineData("Docker: 0 năm kinh nghiệm.")]
    [InlineData("Docker: Chưa có kinh nghiệm.")]
    [InlineData("Docker: No work experience.")]
    [InlineData("Docker: 0 years of experience, biết C#.")]
    [InlineData("Chưa có kinh nghiệm, với Docker.")]
    [InlineData("Hướng dẫn dành cho người chưa có kinh nghiệm.")]
    [InlineData("HỌC VẤN\nSinh viên học C# 0 năm.")]
    [InlineData("Sinh viên mới tốt nghiệp, khóa học 0 tháng.")]
    [InlineData("DỰ ÁN\nThời gian 0 tháng.")]
    public void Missing_or_skill_specific_information_does_not_imply_zero_years(string text)
    {
        Assert.Null(Years(text));
    }

    [Theory]
    [InlineData("0 năm", "0")]
    [InlineData("0 năm kinh nghiệm", "0")]
    [InlineData("0 years of experience", "0")]
    [InlineData("0 tháng kinh nghiệm", "0")]
    [InlineData("Kinh nghiệm: 0 năm", "0")]
    [InlineData("Kinh nghiệm làm việc: 0 năm", "0")]
    [InlineData("Work experience: 0 years", "0")]
    [InlineData("Tôi có 0 năm kinh nghiệm", "0")]
    [InlineData("6 tháng", "0.5")]
    [InlineData("6 tháng kinh nghiệm", "0.5")]
    [InlineData("1 năm", "1")]
    [InlineData("2 năm", "2")]
    [InlineData("1 năm 6 tháng", "1.5")]
    [InlineData("0 năm 6 tháng kinh nghiệm", "0.5")]
    public void Explicit_duration_keeps_zero_and_existing_fractional_years(string text, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), Years(text));
    }

    [Theory]
    [InlineData("Chưa có kinh nghiệm")]
    [InlineData("Chưa có kinh nghiệm làm việc.")]
    [InlineData("Không có kinh nghiệm.")]
    [InlineData("Kinh nghiệm: Chưa có kinh nghiệm.")]
    [InlineData("KINH NGHIỆM\n- Chưa có kinh nghiệm.")]
    [InlineData("Sinh viên mới tốt nghiệp, chưa có kinh nghiệm.")]
    [InlineData("Sinh viên mới tốt nghiệp, 0 năm kinh nghiệm.")]
    [InlineData("Tôi chưa có kinh nghiệm làm việc.")]
    [InlineData("GIỚI THIỆU\nChưa có kinh nghiệm.")]
    [InlineData("No work experience.")]
    [InlineData("I have no prior experience.")]
    [InlineData("Tôi có 0 năm kinh nghiệm, thành thạo C#.")]
    [InlineData("Chưa có kinh nghiệm, mong muốn học hỏi.")]
    public void Explicit_statement_of_no_experience_is_zero(string text)
    {
        Assert.Equal(0m, Years(text));
    }

    [Theory]
    [InlineData("0 năm kinh nghiệm. 2 năm làm Backend.")]
    [InlineData("2 năm làm Backend. Chưa có kinh nghiệm.")]
    [InlineData("Chưa có kinh nghiệm.\nKINH NGHIỆM\nCông ty A 01/2024 - 12/2025")]
    public void Zero_statement_does_not_replace_larger_work_evidence(string text)
    {
        // Quy tắc A9 hiện có vẫn lấy giá trị lớn nhất giữa thời lượng và các khoảng công việc hợp lệ.
        Assert.Equal(2m, Years(text));
    }

    [Theory]
    [InlineData("2025-11-30", "0.5")]
    [InlineData("2026-05-01", "1")]
    [InlineData("2026-11-11", "1.5")]
    public void Present_range_uses_the_supplied_date(string today, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            Years("KINH NGHIỆM\nCông ty A 06/2025 – nay", DateOnly.Parse(today, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("C#.", null, 100.0)]
    [InlineData("C#. 0 năm kinh nghiệm.", 0.0, 85.71)]
    [InlineData("C#. Chưa có kinh nghiệm.", 0.0, 85.71)]
    [InlineData("C#. 6 tháng kinh nghiệm.", 0.5, 89.29)]
    [InlineData("C#. 1 năm kinh nghiệm.", 1.0, 92.86)]
    [InlineData("C#. 2 năm kinh nghiệm.", 2.0, 100.0)]
    public void Known_zero_keeps_experience_in_denominator_while_missing_information_is_redistributed(
        string text, double? expectedYears, double expectedScore)
    {
        var result = Score(text);
        decimal? years = expectedYears is { } value ? (decimal)value : null;

        Assert.Equal(years, result.YearsExperience);
        Assert.Equal((decimal)expectedScore, result.Score);
        Assert.True(result.HasApplicableCriteria);
        Assert.Equal(expectedYears is null,
            result.Notes.Any(note => note.StartsWith("CV không ghi rõ thời gian kinh nghiệm", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("C#.")]
    [InlineData("C#. 0 năm kinh nghiệm.")]
    [InlineData("C#. Chưa có kinh nghiệm.")]
    [InlineData("C#. 6 tháng kinh nghiệm.")]
    public void Job_without_experience_requirement_does_not_penalize_the_candidate(string text)
    {
        var result = Score(text, requiredYears: 0m);

        Assert.Equal(100m, result.Score);
        Assert.Contains("Vị trí không yêu cầu số năm kinh nghiệm.", result.Notes);
    }

    [Theory]
    [InlineData("C#. 0 năm kinh nghiệm với Docker.")]
    [InlineData("C#. 0 năm kinh nghiệm SQL.")]
    [InlineData("C#. Docker: 0 years of experience.")]
    [InlineData("C#. Docker: 0 năm kinh nghiệm.")]
    [InlineData("C#. Docker: Chưa có kinh nghiệm.")]
    [InlineData("C#. Docker: No work experience.")]
    public void Skill_specific_zero_does_not_add_an_overall_experience_penalty(string text)
    {
        var result = Score(text);

        Assert.Null(result.YearsExperience);
        Assert.Equal(100m, result.Score);
        Assert.Contains(result.Notes,
            note => note.StartsWith("CV không ghi rõ thời gian kinh nghiệm", StringComparison.Ordinal));
    }

    [Fact]
    public void Experience_only_job_distinguishes_known_zero_from_no_applicable_data()
    {
        var unknown = Score("Chưa ghi kinh nghiệm.", withSkill: false);
        var knownZero = Score("Chưa có kinh nghiệm.", withSkill: false);

        Assert.Null(unknown.YearsExperience);
        Assert.False(unknown.HasApplicableCriteria);
        Assert.Equal(0m, unknown.Score);
        Assert.Equal(0m, knownZero.YearsExperience);
        Assert.True(knownZero.HasApplicableCriteria);
        Assert.Equal(0m, knownZero.Score);
    }
}
