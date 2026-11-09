using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Scoring;

namespace SmartCv.Tests;

/// <summary>A10: gộp điểm keyword + AI, ngưỡng, giới hạn khi thiếu kỹ năng bắt buộc (hàm thuần).</summary>
public class ScoreCombinerTests
{
    private const decimal Cap = 69m;

    private static SkillMatch Found(string code, bool must = true) => new(code, code.ToUpperInvariant(), must, true, $"trích dẫn {code}");
    private static SkillMatch NotFound(string code, bool must = true) => new(code, code.ToUpperInvariant(), must, false, null);

    private static KeywordScoreResult Keyword(decimal score, params SkillMatch[] skills) =>
        new(score, skills.Length > 0 ? skills : [Found("python")], null, null, []);

    private static AiEvaluationResult Ai(decimal score, params string[] matched) =>
        new() { IsSuccess = true, Score = score, MatchedSkillCodes = matched, Reasoning = "Lý do của AI.", Attempts = 1 };

    private static AiEvaluationResult Failed(AiErrorKind kind, string message) => AiEvaluationResult.Failure(kind, message, 3);

    private static CombinedScore Combine(KeywordScoreResult keyword, AiEvaluationResult? ai, decimal? cap = Cap) =>
        ScoreCombiner.Combine(keyword, ai, 0.3m, 0.7m, 70m, cap);

    [Theory]
    [InlineData(80, 90, 87)]
    [InlineData(100, 100, 100)]
    [InlineData(0, 0, 0)]
    [InlineData(33.33, 50, 44.999)]
    public void Success_uses_30_70_formula(decimal keyword, decimal ai, decimal expectedRaw)
    {
        var r = Combine(Keyword(keyword), Ai(ai));

        Assert.Equal(Math.Round(expectedRaw, 2, MidpointRounding.AwayFromZero), r.FinalScore);
        Assert.Equal(ScoreSources.Combined, r.ScoreSource);
        Assert.Equal(ai, r.AiScore);
        Assert.Equal("Lý do của AI.", r.Reasoning);
        Assert.Null(r.ErrorMessage);
    }

    [Theory]
    [InlineData(AiErrorKind.Other, "Gemini báo lỗi")]
    [InlineData(AiErrorKind.Network, "Gemini không phản hồi sau 90 giây")]
    [InlineData(AiErrorKind.QuotaExceeded, "Gemini vượt hạn mức sau 3 lần thử lại")]
    public void Ai_failure_falls_back_to_keyword_only_without_zero_ai_score(AiErrorKind kind, string message)
    {
        var r = Combine(Keyword(80), Failed(kind, message));

        Assert.Null(r.AiScore);
        Assert.Equal(80m, r.FinalScore);
        Assert.Equal(ScoreSources.KeywordOnly, r.ScoreSource);
        Assert.Equal(message, r.ErrorMessage);
        Assert.Equal($"{message}; điểm chỉ tính theo từ khóa.", r.Reasoning);
    }

    [Fact]
    public void No_ai_result_is_keyword_only() => Assert.Equal(ScoreSources.KeywordOnly, Combine(Keyword(50), null).ScoreSource);

    [Fact]
    public void Missing_must_have_caps_final_score_and_explains()
    {
        var r = Combine(Keyword(90, Found("python"), NotFound("ml")), Ai(90, "python"));

        Assert.Equal(Cap, r.FinalScore);
        Assert.False(r.PredictedPass);
        Assert.Equal(FitLevels.Potential, r.FitLabel);
        Assert.Contains("giới hạn 69", r.Reasoning);
        Assert.Contains("ML", r.Reasoning);
    }

    [Fact]
    public void Cap_also_applies_to_keyword_only_results()
    {
        var r = Combine(Keyword(90, Found("python"), NotFound("ml")), Failed(AiErrorKind.Network, "lỗi"));
        Assert.Equal(Cap, r.FinalScore);
        Assert.Equal(ScoreSources.KeywordOnly, r.ScoreSource);
    }

    [Fact]
    public void Full_must_have_is_not_capped()
    {
        var r = Combine(Keyword(90, Found("python"), NotFound("docker", must: false)), Ai(90, "python"));
        Assert.Equal(90m, r.FinalScore);
        Assert.DoesNotContain("giới hạn", r.Reasoning);
    }

    [Fact]
    public void Must_have_confirmed_by_ai_is_not_capped()
    {
        var r = Combine(Keyword(90, Found("python"), NotFound("ml")), Ai(90, "python", "ml"));
        Assert.Equal(90m, r.FinalScore);
    }

    [Fact]
    public void Score_below_cap_is_unchanged()
    {
        var r = Combine(Keyword(40, NotFound("ml")), Ai(50));
        Assert.Equal(47m, r.FinalScore);
        Assert.DoesNotContain("giới hạn", r.Reasoning);
    }

    [Fact]
    public void Null_cap_disables_the_rule()
    {
        var r = Combine(Keyword(90, NotFound("ml")), Ai(90), cap: null);
        Assert.Equal(90m, r.FinalScore);
    }

    [Theory]
    [InlineData(100, FitLevels.Excellent, true)]
    [InlineData(85, FitLevels.Excellent, true)]
    [InlineData(84.99, FitLevels.Good, true)]
    [InlineData(70, FitLevels.Good, true)]
    [InlineData(69.99, FitLevels.Potential, false)]
    [InlineData(55, FitLevels.Potential, false)]
    [InlineData(54.99, FitLevels.Poor, false)]
    [InlineData(0, FitLevels.Poor, false)]
    public void Fit_label_and_pass_follow_thresholds(decimal score, string label, bool pass)
    {
        var r = Combine(Keyword(score), Failed(AiErrorKind.Network, "lỗi"));
        Assert.Equal(label, r.FitLabel);
        Assert.Equal(pass, r.PredictedPass);
    }

    [Fact]
    public void Pass_threshold_comes_from_parameter()
    {
        var r = ScoreCombiner.Combine(Keyword(75), Failed(AiErrorKind.Network, "lỗi"), 0.3m, 0.7m, 80m, Cap);
        Assert.False(r.PredictedPass);
    }

    [Fact]
    public void Missing_gpa_and_experience_are_not_invented()
    {
        var r = Combine(Keyword(80), Ai(90));
        Assert.Null(r.YearsExperience);
        Assert.Equal(87m, r.FinalScore);
    }

    [Fact]
    public void Experience_uses_ai_value_then_keyword_value()
    {
        var keyword = Keyword(80) with { YearsExperience = 1m };
        Assert.Equal(2m, Combine(keyword, Ai(90) with { YearsExperience = 2m }).YearsExperience);
        Assert.Equal(1m, Combine(keyword, Ai(90)).YearsExperience);
    }

    [Fact]
    public void Skill_confirmed_only_by_ai_is_labelled_and_has_no_fake_quote()
    {
        var r = Combine(Keyword(50, Found("python"), NotFound("ml")), Ai(60, "ml"));

        var ml = r.Skills.Single(s => s.SkillCode == "ml");
        Assert.True(ml.IsMatched);
        Assert.Equal(SkillMatchSources.Ai, ml.MatchedBy);
        Assert.Equal(ScoreCombiner.AiConfirmedEvidence, ml.Evidence);
    }

    [Fact]
    public void Keyword_match_and_evidence_survive_when_ai_disagrees()
    {
        var ai = Ai(60) with { MissingSkillCodes = ["python"] };
        var python = Combine(Keyword(50, Found("python")), ai).Skills.Single();

        Assert.True(python.IsMatched);
        Assert.Equal(SkillMatchSources.Keyword, python.MatchedBy);
        Assert.Equal("trích dẫn python", python.Evidence);
    }

    [Fact]
    public void Unknown_ai_codes_never_become_skills()
    {
        var r = Combine(Keyword(50, Found("python")), Ai(60, "python", "ma_la"));
        Assert.Equal(["python"], r.Skills.Select(s => s.SkillCode));
    }

    [Fact]
    public void Failed_ai_cannot_confirm_skills()
    {
        var failed = Failed(AiErrorKind.InvalidResponse, "lỗi") with { MatchedSkillCodes = ["ml"] };
        Assert.False(Combine(Keyword(50, NotFound("ml")), failed).Skills.Single().IsMatched);
    }

    [Fact]
    public void Simulated_result_is_distinguishable()
    {
        Assert.True(Combine(Keyword(80), Ai(80) with { IsSimulated = true }).IsSimulated);
        Assert.False(Combine(Keyword(80), Ai(80)).IsSimulated);
    }

    [Theory]
    [InlineData(0.5, 0.7)]
    [InlineData(-0.3, 1.3)]
    public void Invalid_weights_are_rejected(decimal keywordWeight, decimal aiWeight)
    {
        Assert.Throws<ArgumentException>(() => ScoreCombiner.Combine(Keyword(80), Ai(90), keywordWeight, aiWeight, 70m, Cap));
    }
}
