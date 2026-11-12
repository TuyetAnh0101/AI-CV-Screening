using SmartCv.Core.Constants;

namespace SmartCv.Core.Evaluation;

public enum LabelSuggestionStatus
{
    New,
    Same,
    Conflict
}

/// <summary>Suggestions from HR decisions only. Never evaluates AI scores or writes labels.</summary>
public static class LabelSuggestionRules
{
    public static bool? Suggest(string? stage, string? rejectReason) => stage switch
    {
        ApplicationStages.Shortlisted or ApplicationStages.Interview => true,
        ApplicationStages.Rejected when rejectReason is RejectReasons.MissingSkills
            or RejectReasons.NotEnoughExperience or RejectReasons.OtherRequirement => false,
        _ => null
    };

    public static string Explain(string? stage, string? rejectReason) =>
        stage == ApplicationStages.Rejected
            ? $"{ApplicationStages.DisplayName(stage)}: {RejectReasons.DisplayName(rejectReason)}"
            : ApplicationStages.DisplayName(stage);

    public static LabelSuggestionStatus Compare(bool suggested, bool? currentLabel) =>
        currentLabel is null ? LabelSuggestionStatus.New
        : currentLabel == suggested ? LabelSuggestionStatus.Same
        : LabelSuggestionStatus.Conflict;
}
