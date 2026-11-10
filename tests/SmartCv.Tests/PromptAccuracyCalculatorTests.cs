using SmartCv.Core.Constants;
using SmartCv.Core.Evaluation;

namespace SmartCv.Tests;

public class PromptAccuracyCalculatorTests
{
    [Theory]
    [InlineData(true, true, PredictionOutcome.Correct, true, null)]
    [InlineData(false, false, PredictionOutcome.Correct, true, null)]
    [InlineData(true, false, PredictionOutcome.Missed, false, "Missed")]
    [InlineData(false, true, PredictionOutcome.WrongPick, false, "WrongPick")]
    public void Completed_predictions_have_the_expected_hr_outcome(bool hrPass, bool predictedPass,
        PredictionOutcome expected, bool expectedMatch, string? expectedMismatch)
    {
        var row = new LabeledPrediction(1, hrPass, EvaluationStatuses.Completed, predictedPass);
        Assert.Equal(expected, PromptAccuracyCalculator.Classify(row));
        Assert.Equal(expectedMatch, PromptAccuracyCalculator.IsMatch(expected));
        Assert.Equal(expectedMismatch, PromptAccuracyCalculator.MismatchType(expected));
    }

    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Failed", false)]
    [InlineData("Pending", true)]
    [InlineData("Pending", null)]
    [InlineData("Completed", null)]
    public void Failed_pending_or_missing_prediction_is_unscored_even_with_a_stale_prediction(string status, bool? prediction)
    {
        var outcome = PromptAccuracyCalculator.Classify(true, status, prediction);
        Assert.Equal(PredictionOutcome.Unscored, outcome);
        Assert.Null(PromptAccuracyCalculator.IsMatch(outcome));
        Assert.Null(PromptAccuracyCalculator.MismatchType(outcome));
    }

    [Fact]
    public void Failed_cv_stays_in_the_denominator_and_all_categories_partition_the_total()
    {
        var rows = new[]
        {
            new LabeledPrediction(1, true, "Completed", true),
            new LabeledPrediction(2, false, "Completed", false),
            new LabeledPrediction(3, true, "Completed", false),
            new LabeledPrediction(4, false, "Completed", true),
            new LabeledPrediction(5, true, "Failed", true),
            new LabeledPrediction(6, false, "Pending", null),
            new LabeledPrediction(7, true, "Completed", null)
        };
        var result = PromptAccuracyCalculator.Calculate(rows);
        Assert.Equal((7, 2, 1, 1, 3, 4),
            (result.Total, result.Correct, result.Missed, result.WrongPick, result.Unscored, result.Scored));
        Assert.Equal(28.57m, result.AccuracyPercent);
        Assert.Equal(result.Total, result.Correct + result.Missed + result.WrongPick + result.Unscored);
    }

    [Fact]
    public void Keyword_only_completed_predictions_are_compared_and_counted_separately()
    {
        var result = PromptAccuracyCalculator.Calculate(
        [
            new(1, true, "Completed", true, ScoreSources.KeywordOnly),
            new(2, false, "Completed", true, ScoreSources.KeywordOnly),
            new(3, false, "Completed", false, ScoreSources.Combined),
            new(4, true, "Failed", null, ScoreSources.KeywordOnly)
        ]);
        Assert.Equal((4, 2, 1, 1, 2), (result.Total, result.Correct, result.WrongPick, result.Unscored, result.KeywordOnly));
        Assert.Equal(50m, result.AccuracyPercent);
    }

    [Theory]
    [InlineData(1, 3, "33.33")]
    [InlineData(2, 3, "66.67")]
    [InlineData(1, 6, "16.67")]
    [InlineData(1, 32, "3.13")]
    public void Percentage_rounds_to_two_decimal_places_away_from_zero(int correct, int total, string expected)
    {
        var rows = Enumerable.Range(0, total)
            .Select(index => new LabeledPrediction(index + 1, true, "Completed", index < correct));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            PromptAccuracyCalculator.Calculate(rows).AccuracyPercent);
    }

    [Fact]
    public void No_comparable_applications_has_null_percentage_while_all_failed_applications_have_zero()
    {
        var empty = PromptAccuracyCalculator.Calculate([]);
        Assert.Equal(0, empty.Total);
        Assert.Null(empty.AccuracyPercent);
        var failed = PromptAccuracyCalculator.Calculate(
        [
            new(1, true, "Failed", null),
            new(2, false, "Failed", null)
        ]);
        Assert.Equal((2, 0, 2), (failed.Total, failed.Scored, failed.Unscored));
        Assert.Equal(0m, failed.AccuracyPercent);
    }

    [Theory]
    [InlineData(false, 7, 2, 70)]
    [InlineData(true, 8, 1, 80)]
    public void Synthetic_demo_matches_documented_v1_and_v2_then_reacts_to_hr_change_and_restore(
        bool secondVersion, int expectedCorrect, int expectedWrongPick, int expectedAccuracy)
    {
        bool[] hr = [true, true, true, true, true, false, false, false, false, false];
        bool[] predictions = [true, false, true, true, true, !secondVersion, true, false, false, false];
        var rows = hr.Select((label, index) => new LabeledPrediction(index + 1, label, "Completed", predictions[index])).ToArray();
        var original = PromptAccuracyCalculator.Calculate(rows);
        Assert.Equal((10, expectedCorrect, 1, expectedWrongPick, 0),
            (original.Total, original.Correct, original.Missed, original.WrongPick, original.Unscored));
        Assert.Equal((decimal)expectedAccuracy, original.AccuracyPercent);

        var changed = rows.Select(row => row.ApplicationId == 2 ? row with { HrIsPass = false } : row).ToArray();
        var updated = PromptAccuracyCalculator.Calculate(changed);
        Assert.Equal((decimal)(expectedAccuracy + 10), updated.AccuracyPercent);
        Assert.Equal(0, updated.Missed);
        var restored = changed.Select(row => row.ApplicationId == 2 ? row with { HrIsPass = true } : row);
        Assert.Equal(original, PromptAccuracyCalculator.Calculate(restored));
    }

    [Fact]
    public void A_single_pass_source_is_enumerated_once_without_changing_its_inputs()
    {
        var enumerations = 0;
        IEnumerable<LabeledPrediction> Source()
        {
            enumerations++;
            yield return new(1, true, "Completed", true);
            yield return new(2, false, "Completed", false);
        }
        Assert.Equal(100m, PromptAccuracyCalculator.Calculate(Source()).AccuracyPercent);
        Assert.Equal(1, enumerations);
    }
}
