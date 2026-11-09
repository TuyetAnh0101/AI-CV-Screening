using SmartCv.Core.Constants;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;

namespace SmartCv.Tests;

/// <summary>A9: chấm keyword, đọc GPA và kinh nghiệm (hàm thuần, không database).</summary>
public class KeywordScoringTests
{
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly ScoringOptions Options = new();

    private static SkillProfile Must(string code, string name, params string[] aliases) => new(code, name, SkillTypes.MustHave, aliases);
    private static SkillProfile Nice(string code, string name, params string[] aliases) => new(code, name, SkillTypes.NiceToHave, aliases);

    private static JobProfile Job(decimal? minGpa, decimal minYears, params SkillProfile[] skills) => new(1, "Job", null, minGpa, minYears, skills);

    private static KeywordScoreResult Score(JobProfile job, string text) => KeywordScoringService.Score(job, text, Options, Today);

    private static bool Matches(SkillProfile skill, string text) => Score(Job(null, 0, skill), text).Skills[0].IsMatched;

    private static decimal? Gpa(string text) => CvFactExtractor.ExtractGpa(TextNormalizer.Normalize(text));

    private static decimal? Years(string text) => CvFactExtractor.ExtractYearsOfExperience(TextNormalizer.Normalize(text), Today);

    private static readonly JobProfile Backend = Job(2.5m, 0.5m,
        Must("csharp", "C#"), Must("aspnetcore", "ASP.NET Core", "ASP.NET"), Must("sqlserver", "SQL Server", "T-SQL"),
        Nice("git", "Git", "GitHub"), Nice("docker", "Docker"));

    // ---------- Công thức ----------

    [Fact]
    public void Full_cv_scores_100()
    {
        var r = Score(Backend, "GPA 3.2. 1.5 năm làm C#, ASP.NET Core, SQL Server, Git, Docker.");
        Assert.Equal(100m, r.Score);
        Assert.All(r.Skills, s => { Assert.True(s.IsMatched); Assert.NotNull(s.Evidence); });
    }

    [Fact]
    public void Missing_one_must_have_reduces_only_that_share()
    {
        var r = Score(Backend, "GPA 3.2. 1.5 năm làm C#, ASP.NET Core, Git, Docker.");
        // (0.6 × 2/3 + 0.2 + 0.1 + 0.1) / 1 = 0.8
        Assert.Equal(80m, r.Score);
        Assert.Equal(["sqlserver"], r.MissingMustHaveCodes);
        Assert.Contains("Thiếu kỹ năng bắt buộc: SQL Server.", r.Notes);
        Assert.Null(r.Skills.Single(s => s.SkillCode == "sqlserver").Evidence);
    }

    [Fact]
    public void Missing_all_skills_keeps_gpa_and_experience_share()
    {
        var r = Score(Backend, "GPA 3.2. 1.5 năm làm kế toán.");
        Assert.Equal(20m, r.Score);
        Assert.Equal(3, r.MissingMustHaveCodes.Count());
    }

    [Fact]
    public void Cv_without_nice_to_have_loses_that_share()
    {
        var r = Score(Backend, "GPA 3.2. 1.5 năm làm C#, ASP.NET Core, SQL Server.");
        Assert.Equal(80m, r.Score);
    }

    [Fact]
    public void Job_without_nice_to_have_drops_the_criterion()
    {
        var job = Job(null, 0, Must("python", "Python"));
        Assert.Equal(100m, Score(job, "Python").Score);
    }

    [Fact]
    public void Nice_to_have_is_not_counted_as_must_have()
    {
        var job = Job(null, 0, Must("python", "Python"), Nice("sql", "SQL"));
        var r = Score(job, "Chỉ biết SQL.");
        Assert.Equal(25m, r.Score);   // 0.2 / (0.6 + 0.2)
        Assert.Equal(["python"], r.MissingMustHaveCodes);
    }

    [Fact]
    public void Missing_gpa_is_removed_from_denominator_and_noted()
    {
        var r = Score(Backend, "1.5 năm làm C#, ASP.NET Core, SQL Server, Git.");
        // (0.6 + 0.2 × 1/2 + 0.1) / (0.6 + 0.2 + 0.1) = 0.8 / 0.9
        Assert.Equal(88.89m, r.Score);
        Assert.Null(r.Gpa);
        Assert.Contains(r.Notes, n => n.Contains("CV không ghi GPA") && n.Contains("chờ phê duyệt"));
    }

    [Fact]
    public void Missing_experience_is_removed_from_denominator_and_noted()
    {
        var r = Score(Backend, "GPA 3.2. Biết C#, ASP.NET Core, SQL Server, Git.");
        Assert.Equal(88.89m, r.Score);
        Assert.Null(r.YearsExperience);
        Assert.Contains(r.Notes, n => n.Contains("kinh nghiệm") && n.Contains("chờ phê duyệt"));
    }

    [Fact]
    public void Job_without_gpa_requirement_ignores_gpa_even_when_low()
    {
        var job = Job(null, 0, Must("python", "Python"));
        var r = Score(job, "GPA 1.0. Python.");
        Assert.Equal(100m, r.Score);
        Assert.Equal(1.0m, r.Gpa);
        Assert.Contains("Vị trí không yêu cầu GPA.", r.Notes);
    }

    [Fact]
    public void Gpa_and_experience_below_requirement_score_proportionally()
    {
        var job = Job(3.0m, 2m, Must("python", "Python"));
        var r = Score(job, "GPA 1.5. Python. 1 năm kinh nghiệm.");
        // (0.6 + 0.1 × 0.5 + 0.1 × 0.5) / 0.8 = 87.5
        Assert.Equal(87.5m, r.Score);
    }

    [Fact]
    public void Demo_application_7_matches_the_guide()
    {
        var job = Job(3.0m, 0, Must("python", "Python", "Python3"), Must("machine_learning", "Machine Learning", "ML", "Học máy"),
            Must("llm_api", "LLM API", "Gemini", "OpenAI API"), Nice("prompt_engineering", "Prompt Engineering", "Prompt"),
            Nice("sql", "SQL", "MySQL", "SQL Server"), Nice("nlp", "NLP", "Xử lý ngôn ngữ tự nhiên"));

        var r = Score(job, "GPA 3.10. Lập trình Python cơ bản, phân tích dữ liệu bằng Excel. Có tham gia CLB lập trình.");

        Assert.Equal(33.33m, r.Score);
        Assert.Equal(3.10m, r.Gpa);
        Assert.Null(r.YearsExperience);
        Assert.Equal(["machine_learning", "llm_api"], r.MissingMustHaveCodes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData(null)]
    public void Empty_cv_is_rejected_instead_of_scoring_zero(string? text)
    {
        Assert.Throws<ArgumentException>(() => KeywordScoringService.Score(Backend, text, Options, Today));
    }

    [Fact]
    public void Job_with_no_applicable_criteria_is_flagged()
    {
        var r = Score(Job(null, 0), "Python");
        Assert.False(r.HasApplicableCriteria);
        Assert.Equal(0m, r.Score);
    }

    [Theory]
    [InlineData("GPA 4.0. 40 năm C#, ASP.NET Core, SQL Server, Git, Docker.")]
    [InlineData("GPA 0.1. 1 tháng.")]
    [InlineData("không có gì")]
    public void Score_is_always_between_0_and_100(string text)
    {
        Assert.InRange(Score(Backend, text).Score, 0m, 100m);
    }

    [Fact]
    public void Default_weights_keep_the_business_ratios()
    {
        var o = new ScoringOptions();
        Assert.Equal((0.3m, 0.7m), (o.KeywordWeight, o.AiWeight));
        Assert.Equal((0.6m, 0.2m, 0.1m, 0.1m), (o.KeywordMustHaveWeight, o.KeywordNiceToHaveWeight, o.KeywordGpaWeight, o.KeywordExperienceWeight));
        Assert.True(o.HasValidWeights());
    }

    [Fact]
    public void Weights_not_summing_to_one_are_invalid()
    {
        Assert.False(new ScoringOptions { KeywordWeight = 0.5m }.HasValidWeights());
        Assert.False(new ScoringOptions { KeywordGpaWeight = 0.2m }.HasValidWeights());
    }

    [Fact]
    public void Weights_are_read_from_options()
    {
        var options = new ScoringOptions { KeywordMustHaveWeight = 0.2m, KeywordNiceToHaveWeight = 0.8m, KeywordGpaWeight = 0, KeywordExperienceWeight = 0 };
        var job = Job(null, 0, Must("python", "Python"), Nice("sql", "SQL"));
        Assert.Equal(80m, KeywordScoringService.Score(job, "SQL", options, Today).Score);
    }

    // ---------- So khớp kỹ năng ----------

    [Theory]
    [InlineData("Thành thạo Java.", true)]
    [InlineData("Java, Spring", true)]
    [InlineData("Thành thạo JavaScript.", false)]
    public void Java_does_not_match_javascript(string text, bool expected) => Assert.Equal(expected, Matches(Must("java", "Java"), text));

    [Theory]
    [InlineData("Dùng SQL hằng ngày", true)]
    [InlineData("Dùng MySQL hằng ngày", false)]
    [InlineData("Dùng NoSQL hằng ngày", false)]
    public void Sql_does_not_match_mysql_without_alias(string text, bool expected) => Assert.Equal(expected, Matches(Must("sql", "SQL"), text));

    [Fact]
    public void Sql_matches_mysql_through_job_alias() => Assert.True(Matches(Must("sql", "SQL", "MySQL"), "Dùng MySQL"));

    [Theory]
    [InlineData("Lập trình C#.", true)]
    [InlineData("Lập trình c# và Java", true)]
    [InlineData("Lập trình C♯", true)]
    [InlineData("Lập trình C++", false)]
    [InlineData("Lập trình C", false)]
    public void Csharp_requires_the_special_character(string text, bool expected) => Assert.Equal(expected, Matches(Must("csharp_x", "C#"), text));

    [Theory]
    [InlineData("Dùng C#", false)]
    [InlineData("Dùng C++", false)]
    [InlineData("Dùng C, Pascal", true)]
    public void Plain_c_does_not_match_csharp_or_cpp(string text, bool expected) => Assert.Equal(expected, Matches(Must("c", "C"), text));

    [Theory]
    [InlineData("Web bằng ASP.NET Core 8", true)]
    [InlineData("Web bằng asp.net\ncore", true)]
    [InlineData("Web bằng ASPXNET Core", false)]
    public void Aspnet_core_treats_the_dot_literally(string text, bool expected) => Assert.Equal(expected, Matches(Must("aspnetcore_x", "ASP.NET Core"), text));

    [Theory]
    [InlineData("Machine Learning")]
    [InlineData("Machine-Learning")]
    [InlineData("machine\nlearning")]
    [InlineData("MACHINE  –  LEARNING")]
    public void Phrases_match_across_spaces_hyphens_and_lines(string text) => Assert.True(Matches(Must("ml_x", "Machine Learning"), text));

    [Theory]
    [InlineData("Kiểm thử thủ công")]
    [InlineData("KIỂM THỬ THỦ CÔNG")]
    [InlineData("KIEM THU THU CONG")]
    [InlineData("kiem thu\nthu cong")]
    [InlineData("Kiểm thử thủ công")]
    public void Vietnamese_matches_with_or_without_diacritics(string text) => Assert.True(Matches(Must("manual", "Kiểm thử thủ công"), text));

    [Fact]
    public void Skill_is_found_by_code_and_alias()
    {
        Assert.True(Matches(Must("machine_learning", "Tên khác"), "machine learning"));
        Assert.True(Matches(Must("x", "Tên khác", "Học máy"), "Nghiên cứu HOC MAY"));
    }

    [Fact]
    public void Hyphenated_alias_matches_spaced_form() => Assert.True(Matches(Must("x", "Tên khác", "T-SQL"), "viết T SQL"));

    // ---------- Bằng chứng ----------

    [Fact]
    public void Evidence_comes_from_original_text_with_diacritics()
    {
        var text = "Tóm tắt: sinh viên năm cuối. Đã làm KIỂM THỬ thủ công cho ứng dụng di động của công ty Việt Nam.";
        var evidence = Score(Job(null, 0, Must("t", "Kiểm thử thủ công")), text).Skills[0].Evidence!;

        Assert.Contains("KIỂM THỬ thủ công", evidence);
        Assert.Contains(evidence.Trim('…'), text);
    }

    [Fact]
    public void Evidence_keeps_special_characters_collapses_lines_and_has_reasonable_length()
    {
        var text = new string('a', 300) + " ứng viên 😀 dùng\nC#\nmỗi ngày 😀 " + string.Join(' ', Enumerable.Repeat("từ", 200));
        var evidence = Score(Job(null, 0, Must("csharp_x", "C#")), text).Skills[0].Evidence!;

        Assert.Contains("C#", evidence);
        Assert.DoesNotContain('\n', evidence);
        Assert.InRange(evidence.Length, 2, 500);
        Assert.StartsWith("…", evidence);
        Assert.EndsWith("…", evidence);
        for (var i = 0; i < evidence.Length; i++)
        {
            if (char.IsHighSurrogate(evidence[i])) Assert.True(char.IsLowSurrogate(evidence[i + 1]));
            if (char.IsLowSurrogate(evidence[i])) Assert.True(i > 0 && char.IsHighSurrogate(evidence[i - 1]));
        }
    }

    [Fact]
    public void Evidence_offsets_stay_correct_for_decomposed_text()
    {
        var text = "Kiến thức Python cơ bản";
        var evidence = Score(Job(null, 0, Must("python", "Python")), text).Skills[0].Evidence!;
        Assert.Equal("Kiến thức Python cơ bản", evidence);
    }

    [Fact]
    public void Long_cv_is_scored_quickly()
    {
        var text = string.Join(' ', Enumerable.Repeat("Công ty phần mềm 06/2024 lập trình viên - - - - giải pháp", 20000));
        Assert.True(text.Length > 1_000_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Score(Backend, text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Mất {watch.Elapsed}");
    }

    // ---------- GPA ----------

    [Theory]
    [InlineData("GPA 3.2/4", "3.2")]
    [InlineData("GPA: 3.20", "3.20")]
    [InlineData("GPA 3,20/4.0", "3.20")]
    [InlineData("gpa = 3.5 / 4", "3.5")]
    [InlineData("CPA: 3.1", "3.1")]
    [InlineData("Điểm trung bình tích lũy: 3.45", "3.45")]
    [InlineData("DIEM TRUNG BINH 3.45", "3.45")]
    [InlineData("GPA (thang 4): 3.3", "3.3")]
    [InlineData("GPA\n3.3", "3.3")]
    [InlineData("GPA đạt 3.6", "3.6")]
    public void Gpa_on_scale_4(string text, string expected) => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Gpa(text));

    [Theory]
    [InlineData("GPA 8.1/10", "3.24")]
    [InlineData("Điểm trung bình 7,5/10", "3.00")]
    [InlineData("GPA hệ 10: 8.0", "3.20")]
    [InlineData("ĐTB 8.0", "3.20")]
    public void Gpa_on_scale_10_is_converted(string text, string expected) => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Gpa(text));

    [Theory]
    [InlineData("Không ghi điểm số")]
    [InlineData("GPA tốt")]
    [InlineData("GPA 11")]
    [InlineData("GPA 5/4")]
    [InlineData("GPA 3.2/5")]
    [InlineData("GPA 85/100")]
    [InlineData("GPA 0")]
    [InlineData("Công ty SGPA 3.2")]
    public void Gpa_is_null_when_missing_or_invalid(string text) => Assert.Null(Gpa(text));

    // ---------- Kinh nghiệm ----------

    [Theory]
    [InlineData("1.5 năm làm Backend", "1.5")]
    [InlineData("1,5 năm kinh nghiệm", "1.5")]
    [InlineData("2+ years of experience", "2")]
    [InlineData("6 tháng thực tập", "0.5")]
    [InlineData("18 months at FPT", "1.5")]
    [InlineData("3 tháng kiểm thử", "0.3")]
    [InlineData("1 năm 6 tháng làm việc", "1.5")]
    [InlineData("2 years and 3 months", "2.3")]
    [InlineData("GPA 3.20. 1.5 năm làm Backend", "1.5")]
    public void Explicit_durations(string text, string expected) => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Years(text));

    [Theory]
    [InlineData("Công ty A (06/2024 - nay)", "2.5")]            // 30 tháng
    [InlineData("Công ty A 06/2024 – hiện tại", "2.5")]
    [InlineData("FPT 06/2024 to present", "2.5")]
    [InlineData("FPT Jun 2024 - Present", "2.5")]
    [InlineData("FPT June, 2024 - Now", "2.5")]
    [InlineData("FPT tháng 6 năm 2024 - nay", "2.5")]
    [InlineData("FPT T6/2024 đến nay", "2.5")]
    [InlineData("Công ty A 2020 - 2022", "3")]                  // 01/2020 đến 12/2022
    [InlineData("Công ty A 01/2021 - 06/2021\nCông ty B 01/2022 - 06/2022", "1")]
    public void Date_ranges(string text, string expected) => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Years(text));

    [Fact]
    public void Overlapping_jobs_count_once()
    {
        Assert.Equal(1m, Years("Công ty A 01/2022 - 12/2022\nCông ty B 06/2022 - 09/2022\nCông ty C 01/2022 - 12/2022"));
    }

    [Fact]
    public void Partly_overlapping_jobs_are_merged()
    {
        Assert.Equal(1.5m, Years("Công ty A 01/2022 - 12/2022\nCông ty B 07/2022 - 06/2023"));
    }

    [Theory]
    [InlineData("Công ty A 13/2024 - 02/2025")]     // tháng sai
    [InlineData("Công ty A 06/2025 - 01/2024")]     // kết thúc trước bắt đầu
    [InlineData("Công ty A 01/2030 - 01/2031")]     // chưa xảy ra
    [InlineData("Công ty A 1900 - 1905")]
    public void Invalid_ranges_are_ignored(string text) => Assert.Null(Years(text));

    [Fact]
    public void End_date_in_future_is_clamped_to_today() => Assert.Equal(0.9m, Years("Công ty A 01/2026 - 12/2030"));

    [Theory]
    [InlineData("Đại học Bách Khoa (2018 - 2022)")]
    [InlineData("Đại học Bách Khoa\n2018 - 2022")]
    [InlineData("2018 - 2022\nĐại học Bách Khoa")]
    [InlineData("HỌC VẤN\nTrường X\n2018 - 2022")]
    [InlineData("EDUCATION\nSome School, 09/2018 - 06/2022")]
    [InlineData("Sinh viên 4 năm ngành CNTT")]
    [InlineData("Năm học 2021 - 2022")]
    [InlineData("Hoàn thành khóa học 6 tháng về lập trình")]
    [InlineData("Online course 3 months")]
    [InlineData("Bootcamp 6 months")]
    [InlineData("Tự học C# 2 năm")]
    public void Study_time_is_not_experience(string text) => Assert.Null(Years(text));

    [Fact]
    public void Education_section_ends_at_the_next_heading()
    {
        Assert.Equal(1m, Years("HỌC VẤN\nTrường X\n2018 - 2022\nKINH NGHIỆM LÀM VIỆC\nCông ty A\n01/2023 - 12/2023"));
    }

    [Fact]
    public void Work_entry_after_a_school_entry_still_counts()
    {
        Assert.Equal(1m, Years("Đại học Bách Khoa\n2018 - 2022\nCông ty FPT\n01/2023 - 12/2023"));
    }

    [Theory]
    [InlineData("Địa chỉ: Quận 7 Nam Định")]
    [InlineData("Quê ở Lô 5 Nam Định")]
    [InlineData("Số 12 Nam Kỳ Khởi Nghĩa")]
    [InlineData("Ngày 3 tháng 5 năm 2024")]
    [InlineData("Mục tiêu: trong 3 năm tới trở thành Senior")]
    [InlineData("Giới tính: Nam")]
    [InlineData("Sinh năm 2002")]
    public void Unrelated_numbers_are_not_experience(string text) => Assert.Null(Years(text));

    [Fact]
    public void Other_sections_only_count_explicit_experience_sentences()
    {
        Assert.Null(Years("DỰ ÁN\nWebsite bán hàng (2022 - 2023)\nThời gian 6 tháng"));
        Assert.Equal(2m, Years("GIỚI THIỆU\nHơn 2 năm kinh nghiệm lập trình"));
    }

    [Fact]
    public void Sample_cv_with_sections_reads_only_work_period()
    {
        var text = "MỤC TIÊU NGHỀ NGHIỆP\nTrở thành lập trình viên.\nHỌC VẤN\nĐại học X (2021 - 2025), GPA: 3.15/4.0\n" +
                   "KINH NGHIỆM LÀM VIỆC\nCông ty Sao Việt (06/2024 - nay), Lập trình viên.\nKỸ NĂNG\nC#\nDỰ ÁN\nWebsite (đồ án tốt nghiệp).";
        Assert.Equal(2.5m, Years(text));
        Assert.Equal(3.15m, Gpa(text));
    }

    [Fact]
    public void Missing_experience_is_null_not_zero() => Assert.Null(Years("Thành thạo C#, chưa ghi thời gian."));

    // ---------- Chuẩn hóa ----------

    [Fact]
    public void Normalizer_keeps_length_and_folds_vietnamese()
    {
        const string text = "ĐẶNG Thị Ánh – C♯ 😀";
        var normalized = TextNormalizer.Normalize(text);
        Assert.Equal(text.Length, normalized.Length);
        Assert.StartsWith("dang thi anh - c#", normalized);
    }

    [Fact]
    public void Normalizer_survives_lone_surrogate()
    {
        var text = TextNormalizer.Compose("a\uD800b");
        Assert.Equal("a b", TextNormalizer.Normalize(text));
    }
}
