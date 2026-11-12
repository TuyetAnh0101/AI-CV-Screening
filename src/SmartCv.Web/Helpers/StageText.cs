using SmartCv.Core.Constants;

namespace SmartCv.Web.Helpers;

public static class StageText
{
    public static string Name(string? stage) => ApplicationStages.DisplayName(stage);
    public static string Reason(string? reason) => RejectReasons.DisplayName(reason);
    public static string Css(string? stage) => stage switch
    {
        ApplicationStages.Reviewing => "stage-reviewing",
        ApplicationStages.Shortlisted => "stage-shortlisted",
        ApplicationStages.Interview => "stage-interview",
        ApplicationStages.Rejected => "stage-rejected",
        ApplicationStages.OnHold => "stage-onhold",
        _ => "stage-new"
    };
}
