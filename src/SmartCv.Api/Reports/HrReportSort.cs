namespace SmartCv.Api.Reports;

/// <summary>Deterministic sorting of the complete, workspace-bounded cohort BEFORE paging.</summary>
public static class HrReportSort
{
    public static readonly string[] Allowed = ["appliedAt", "job", "stage", "score", "applicant", "applicationId"];

    public static List<HrReportFact> Order(IReadOnlyList<HrReportFact> facts, string sort, bool descending)
    {
        IOrderedEnumerable<HrReportFact> result = (sort, descending) switch
        {
            ("job", true) => facts.OrderByDescending(r => r.Application.JobTitle, StringComparer.OrdinalIgnoreCase),
            ("job", false) => facts.OrderBy(r => r.Application.JobTitle, StringComparer.OrdinalIgnoreCase),
            ("stage", true) => facts.OrderByDescending(r => r.Application.Stage, StringComparer.Ordinal),
            ("stage", false) => facts.OrderBy(r => r.Application.Stage, StringComparer.Ordinal),
            ("score", true) => facts.OrderByDescending(r => r.Scored ? r.Score!.Score : null),
            ("score", false) => facts.OrderBy(r => r.Scored ? r.Score!.Score : null),
            ("applicant", true) => facts.OrderByDescending(r => r.Application.CandidateName ?? "", StringComparer.OrdinalIgnoreCase),
            ("applicant", false) => facts.OrderBy(r => r.Application.CandidateName ?? "", StringComparer.OrdinalIgnoreCase),
            ("applicationId", true) => facts.OrderByDescending(r => r.Application.ApplicationId),
            ("applicationId", false) => facts.OrderBy(r => r.Application.ApplicationId),
            ("appliedAt", false) => facts.OrderBy(r => r.Application.CreatedAtUtc),
            _ => facts.OrderByDescending(r => r.Application.CreatedAtUtc)
        };
        return result.ThenBy(r => r.Application.ApplicationId).ToList();
    }
}
