using SmartCv.Core.Ai;
using SmartCv.Core.Scoring;
using System.IO.Compression;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace SmartCv.Tests.Scoring;

public class ReviewFlagsTests(ITestOutputHelper output)
{
    private static ReviewInput Input(decimal keyword = 80, decimal? ai = 80, decimal final = 80,
        int? length = 500, string status = "Completed", string source = "Combined") =>
        new(status, keyword, ai, final, source, length, .3m, .7m, false, null);
    private static string[] Codes(ReviewInput input, ReviewFlagOptions? options = null) =>
        ReviewFlags.Evaluate(input, options ?? new()).Select(f => f.Code).ToArray();

    [Theory]
    [InlineData(29.99, false)] [InlineData(30, true)] [InlineData(-30, true)]
    public void Score_gap_has_inclusive_absolute_boundary(decimal gap, bool flagged) =>
        Assert.Equal(flagged, Codes(Input(keyword: 50 + gap, ai: 50)).Contains("ScoreGap"));

    [Theory]
    [InlineData(499, true)] [InlineData(500, false)] [InlineData(0, true)] [InlineData(null, false)]
    public void Text_length_boundary_does_not_assume_unknown_length_is_zero(int? length, bool flagged) =>
        Assert.Equal(flagged, Codes(Input(length: length)).Contains("ShortText"));

    [Theory]
    [InlineData("Pending")] [InlineData("Failed")]
    public void Unfinished_or_failed_evaluations_never_get_flags(string status) =>
        Assert.Empty(Codes(Input(10, 90, 69, 10, status)));

    [Theory]
    [InlineData("KeywordOnly")] [InlineData("Combined")] [InlineData("")]
    public void Missing_AI_with_keyword_is_reported_without_a_gap(string source) =>
        Assert.Equal(new[] { "KeywordOnly" }, Codes(Input(ai: null, source: source)));

    [Theory]
    [InlineData(79.99, false)] [InlineData(80, true)]
    public void Real_cap_from_ScoreCombiner_and_saved_missing_skills_is_required(decimal aiScore, bool flagged)
    {
        var combined = ScoreCombiner.Combine(new(90, [new("M", "Synthetic skill", true, false, null)], null, null, []),
            new AiEvaluationResult { IsSuccess = true, Score = aiScore }, .3m, .7m, 70, 69);
        var input = Input(90, aiScore, combined.FinalScore) with { HasSavedMissingMustHave = true, Reasoning = combined.Reasoning };
        Assert.Equal(flagged, Codes(input).Contains("CappedHighAi"));
    }

    [Fact]
    public void Low_final_or_current_cap_value_alone_never_proves_a_cap()
    {
        Assert.DoesNotContain("CappedHighAi", Codes(Input(90, 90, 69)));
        var actual = Input(90, 90, 69) with { HasSavedMissingMustHave = true };
        Assert.DoesNotContain("CappedHighAi", Codes(actual)); // no saved audit marker
        actual = actual with { Reasoning = "(Điểm cuối giới hạn 69 vì thiếu kỹ năng bắt buộc: Synthetic.)" };
        Assert.Contains("CappedHighAi", Codes(actual));
        Assert.DoesNotContain("CappedHighAi", Codes(actual with { HasSavedMissingMustHave = false }));
        Assert.DoesNotContain("CappedHighAi", Codes(actual with { KeywordScore = 20, AiScore = 90 })); // weighted exactly 69
        Assert.DoesNotContain("CappedHighAi", Codes(actual with { KeywordScore = 19.99m, AiScore = 90 })); // rounds to 69
        Assert.DoesNotContain("CappedHighAi", Codes(actual with { ScoreSource = "KeywordOnly" }));
    }

    [Fact]
    public void Historical_custom_cap_is_detected_without_using_todays_scoring_defaults()
    {
        var combined = ScoreCombiner.Combine(new(90, [new("M", "Synthetic skill", true, false, null)], null, null, []),
            new AiEvaluationResult { IsSuccess = true, Score = 90 }, .4m, .6m, 70, 65.5m);
        Assert.Contains("CappedHighAi", Codes(Input(90, 90, 65.5m) with
        { KeywordWeight = .4m, AiWeight = .6m, HasSavedMissingMustHave = true, Reasoning = combined.Reasoning }));
    }

    [Fact]
    public void Multiple_flags_are_distinct_and_never_copy_reasoning_or_CV_identity()
    {
        var flags = ReviewFlags.Evaluate(Input(40, 90, 69, 499) with
        { HasSavedMissingMustHave = true, Reasoning = "sensitive@example.test (Điểm cuối giới hạn 69 vì thiếu kỹ năng bắt buộc: Private skill.)" }, new());
        Assert.Equal(new[] { "ScoreGap", "ShortText", "CappedHighAi" }, flags.Select(f => f.Code));
        Assert.All(flags, f => { Assert.DoesNotContain("sensitive", f.Message); Assert.DoesNotContain("Private", f.Message); });
    }

    [Fact]
    public void Configured_thresholds_are_used()
    {
        Assert.Equal(new[] { "ScoreGap", "ShortText" }, Codes(Input(70, 85, 80.5m),
            new() { ScoreGap = 10, MinTextLength = 501, CappedHighAiScore = 90 }));
    }

    [A40DemoFact]
    public void Demo_17_and_18_are_PDF_data_and_only_text_length_is_observed_without_AI()
    {
        using var archive = ZipFile.OpenRead(Environment.GetEnvironmentVariable(A40DemoFactAttribute.Setting)!);
        foreach (var number in new[] { "17-", "18-" })
        {
            var entry = archive.Entries.Single(e => e.FullName.Contains("cv-20-backend/") && e.Name.StartsWith(number));
            using var stream = entry.Open(); using var memory = new MemoryStream(); stream.CopyTo(memory);
            using var pdf = PdfDocument.Open(memory.ToArray());
            var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
            Assert.NotEmpty(text);
            var flags = Codes(Input(ai: null, length: text.Length, source: "KeywordOnly"));
            Assert.DoesNotContain("ScoreGap", flags); Assert.DoesNotContain("CappedHighAi", flags);
            if (number == "18-") Assert.Contains("ShortText", flags);
            else Assert.DoesNotContain("ShortText", flags);
            output.WriteLine($"Demo CV {number[..2]}: extracted characters={text.Length}; no AI scoring performed.");
        }
    }
}

public sealed class A40DemoFactAttribute : FactAttribute
{
    public const string Setting = "SMARTCV_A40_DEMO_ZIP";
    public A40DemoFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable(Setting))) Skip = $"Set {Setting} to inspect demo PDFs 17 and 18.";
    }
}
