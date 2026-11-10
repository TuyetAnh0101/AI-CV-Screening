using SmartCv.Core.Constants;

namespace SmartCv.Core.Evaluation;

/// <summary>A current HR label and the persisted prediction for one application in one run.</summary>
public sealed record LabeledPrediction(
    int ApplicationId,
    bool HrIsPass,
    string Status,
    bool? PredictedPass,
    string? ScoreSource = null);

public enum PredictionOutcome
{
    Correct,
    Missed,
    WrongPick,
    Unscored
}

public static class MismatchTypes
{
    public const string Missed = "Missed";
    public const string WrongPick = "WrongPick";
}

public sealed record AccuracyResult(
    int Total,
    int Correct,
    int Missed,
    int WrongPick,
    int Unscored,
    int KeywordOnly,
    decimal? AccuracyPercent)
{
    public int Scored => Total - Unscored;
}

/// <summary>
/// A16's single accuracy formula. Every labeled input remains in the denominator,
/// including failed, pending and missing predictions. No labels means no percentage.
/// The decision comes from PredictedPass; neither scores nor the prompt are inputs.
/// </summary>
public static class PromptAccuracyCalculator
{
    public static PredictionOutcome Classify(bool hrIsPass, string status, bool? predictedPass)
    {
        if (status != EvaluationStatuses.Completed || predictedPass is null) return PredictionOutcome.Unscored;
        if (hrIsPass == predictedPass.Value) return PredictionOutcome.Correct;
        return hrIsPass ? PredictionOutcome.Missed : PredictionOutcome.WrongPick;
    }

    public static PredictionOutcome Classify(LabeledPrediction prediction) =>
        Classify(prediction.HrIsPass, prediction.Status, prediction.PredictedPass);

    public static bool? IsMatch(PredictionOutcome outcome) => outcome switch
    {
        PredictionOutcome.Correct => true,
        PredictionOutcome.Missed or PredictionOutcome.WrongPick => false,
        _ => null
    };

    public static string? MismatchType(PredictionOutcome outcome) => outcome switch
    {
        PredictionOutcome.Missed => MismatchTypes.Missed,
        PredictionOutcome.WrongPick => MismatchTypes.WrongPick,
        _ => null
    };

    public static AccuracyResult Calculate(IEnumerable<LabeledPrediction> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        int total = 0, correct = 0, missed = 0, wrongPick = 0, unscored = 0, keywordOnly = 0;
        foreach (var prediction in items)
        {
            total++;
            switch (Classify(prediction))
            {
                case PredictionOutcome.Correct: correct++; break;
                case PredictionOutcome.Missed: missed++; break;
                case PredictionOutcome.WrongPick: wrongPick++; break;
                default: unscored++; break;
            }
            if (prediction.Status == EvaluationStatuses.Completed && prediction.ScoreSource == ScoreSources.KeywordOnly)
                keywordOnly++;
        }
        decimal? accuracy = total == 0
            ? null
            : Math.Round(correct * 100m / total, 2, MidpointRounding.AwayFromZero);
        return new(total, correct, missed, wrongPick, unscored, keywordOnly, accuracy);
    }
}
