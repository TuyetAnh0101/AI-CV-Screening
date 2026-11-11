using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

public enum MismatchFilter { All, Missed, WrongPick }

/// <summary>Read-only analysis of one run. Applications (CV + job) are the backend evaluation unit.</summary>
public sealed class MismatchView
{
    public int TruePositive { get; private init; }
    public int FalsePositive { get; private init; }
    public int FalseNegative { get; private init; }
    public int TrueNegative { get; private init; }
    public int Compared => TruePositive + FalsePositive + FalseNegative + TrueNegative;
    public int Excluded { get; private init; }
    public int DuplicateApplications { get; private init; }
    public int InconsistentFlags { get; private init; }
    public int KeywordOnly { get; private init; }
    public bool Complete { get; private init; }
    public bool AllMatch => Complete && FalsePositive + FalseNegative == 0;
    public IReadOnlyList<PromptTestEvaluationDto> Mismatches { get; private init; } = [];
    public IEnumerable<PromptTestEvaluationDto> Filter(MismatchFilter filter) => Mismatches.Where(i =>
        filter == MismatchFilter.All || i.MismatchType == filter.ToString());
    public int Count(MismatchFilter filter) => Filter(filter).Count();

    public static MismatchView Create(PromptTestProgressDto run)
    {
        // Exclude every copy of a duplicate rather than arbitrarily trusting one response row.
        var groups = run.Items.GroupBy(i => i.ApplicationId).ToList();
        var unique = groups.Where(g => g.Key > 0 && g.Count() == 1).Select(g => g.Single()).ToList();
        var valid = unique.Where(HasPrediction).ToList();
        var inconsistent = unique.Count(i => !FlagsAgree(i));
        return new()
        {
            TruePositive = valid.Count(i => i.HrIsPass == true && i.PredictedPass == true),
            FalsePositive = valid.Count(i => i.HrIsPass == false && i.PredictedPass == true),
            FalseNegative = valid.Count(i => i.HrIsPass == true && i.PredictedPass == false),
            TrueNegative = valid.Count(i => i.HrIsPass == false && i.PredictedPass == false),
            Excluded = run.Items.Count - valid.Count,
            DuplicateApplications = groups.Count(g => g.Count() > 1),
            InconsistentFlags = inconsistent,
            KeywordOnly = valid.Count(i => i.ScoreSource == "KeywordOnly"),
            Complete = run.Status == "Completed" && run.TotalItems > 0 && valid.Count == run.TotalItems
                && run.Items.Count == run.TotalItems && inconsistent == 0,
            Mismatches = valid.Where(i => i.IsMatch == false && FlagsAgree(i))
                .OrderBy(i => i.ApplicationId).ToList()
        };
    }

    public static bool HasPrediction(PromptTestEvaluationDto item) => item.Status == "Completed"
        && item.ExtractionStatus != "Failed" && item.HrIsPass.HasValue && item.PredictedPass.HasValue;

    private static bool FlagsAgree(PromptTestEvaluationDto item)
    {
        if (!HasPrediction(item)) return item.IsMatch is null && item.MismatchType is null;
        var match = item.HrIsPass == item.PredictedPass;
        var kind = match ? null : item.HrIsPass == true ? "Missed" : "WrongPick";
        return item.IsMatch == match && item.MismatchType == kind;
    }

    public static PromptTestRunDto? DefaultRun(IEnumerable<PromptTestRunDto> runs, int? activeVersionId)
    {
        var completed = runs.Where(r => r.RunId > 0 && r.Status == "Completed")
            .OrderByDescending(r => r.CompletedAt ?? r.CreatedAt).ThenByDescending(r => r.RunId).ToList();
        return completed.FirstOrDefault(r => r.PromptVersionId == activeVersionId) ?? completed.FirstOrDefault();
    }

    public static bool SupportsDetail(int runId, PromptTestEvaluationDto item, EvaluationDetailDto detail) =>
        item.EvaluationId > 0 && detail.EvaluationId == item.EvaluationId && detail.RunId == runId
        && detail.RunType == "PromptTest" && detail.ApplicationId == item.ApplicationId;
}
