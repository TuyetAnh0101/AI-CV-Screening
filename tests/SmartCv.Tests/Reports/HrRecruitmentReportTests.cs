using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using SmartCv.Api.Reports;
using SmartCv.Core.Constants;
using SmartCv.Infrastructure.Export;
using ClosedXML.Excel;

namespace SmartCv.Tests.Reports;

public sealed class HrRecruitmentReportTests
{
    private static HrReportFact Fact(int id, Guid cv, int? candidate, string stage,
        HrScoreFact? score = null, bool everPassed = false, DateTime? firstDecision = null,
        bool openFlag = false, string? rejectReason = null, string? name = "Demo") =>
        new(new HrApplicationFact(id, cv, candidate, name, 3, "Data Analyst",
                new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc), stage, rejectReason,
                firstDecision, everPassed, openFlag), score);

    [Fact]
    public void Application_cv_and_candidate_are_independent_units()
    {
        var cv = Guid.NewGuid();
        var rows = new[] { Fact(1, cv, 100, ApplicationStages.New),
            Fact(2, cv, 100, ApplicationStages.Rejected),
            Fact(3, Guid.NewGuid(), null, ApplicationStages.Reviewing) };
        var s = HrReportRollup.Summary(rows);
        Assert.Equal(3, s.Applications);
        Assert.Equal(2, s.UniqueCvs);
        Assert.Equal(1, s.KnownUniqueCandidates);
        Assert.Equal(1, s.ApplicationsWithoutCandidate);
        Assert.Equal(0, s.EvaluatedApplications);
    }

    [Fact]
    public void Score_threshold_is_from_selected_screening_run_not_global_constant()
    {
        var rows = new[]
        {
            Fact(1, Guid.NewGuid(), 1, ApplicationStages.Shortlisted,
                new HrScoreFact(1, 7, EvaluationStatuses.Completed, ScoreSources.Combined, 75m, 80m, "Good"), true),
            Fact(2, Guid.NewGuid(), 2, ApplicationStages.New,
                new HrScoreFact(2, 8, EvaluationStatuses.Completed, ScoreSources.KeywordOnly, 75m, 70m, "Good"))
        };
        var s = HrReportRollup.Summary(rows);
        Assert.Equal(2, s.EvaluatedApplications);
        Assert.Equal(1, s.PassedScore);
        Assert.Equal(1, s.BelowThreshold);
        Assert.Equal(1, s.AiCombined);
        Assert.Equal(1, s.KeywordOnly);
        Assert.Equal(1, s.EverPassedCvRound);
        Assert.Equal(50m, s.HrPassedPercent);
        Assert.Equal(75m, s.AverageFinalScore);
    }

    [Fact]
    public void Current_stage_and_ever_passed_are_different_concepts()
    {
        var rows = new[] { Fact(1, Guid.NewGuid(), 1, ApplicationStages.Rejected,
            everPassed: true, rejectReason: RejectReasons.MissingSkills) };
        var s = HrReportRollup.Summary(rows);
        Assert.Equal(1, s.EverPassedCvRound);
        Assert.Equal(100m, s.CurrentRejectedPercent);
        Assert.Equal(1, Assert.Single(s.RejectReasons).Count);
        Assert.Equal(0, s.NeedsAction);
    }

    [Fact]
    public void No_completed_score_is_not_a_zero_and_failures_are_separate()
    {
        var rows = new[]
        {
            Fact(1, Guid.NewGuid(), 1, ApplicationStages.New),
            Fact(2, Guid.NewGuid(), 2, ApplicationStages.Reviewing,
                new HrScoreFact(2, 5, EvaluationStatuses.Failed, null, null, 70, null))
        };
        var s = HrReportRollup.Summary(rows);
        Assert.Null(s.AverageFinalScore);
        Assert.Equal(2, s.NoCompletedEvaluation);
        Assert.Single(rows, x => HrReportRollup.Include(x, "Failed"));
        Assert.Single(rows, x => HrReportRollup.Include(x, "Unscored"));
    }

    [Fact]
    public void Export_is_anonymous_by_default_and_formula_safe()
    {
        var rows = new[] { Fact(1, Guid.NewGuid(), 1, ApplicationStages.New,
            name: "=HYPERLINK(\"bad\",\"click\")") };
        var summary = HrReportRollup.Summary(rows);
        var anonymous = HrReportRollup.Row(rows[0], true);
        var identified = HrReportRollup.Row(rows[0], false);
        Assert.StartsWith("HS-", anonymous.Applicant);
        Assert.StartsWith("=", identified.Applicant);
        var bytes = HrRecruitmentReportExcelWriter.Write(summary, [identified], "test", false, DateTime.UtcNow);
        using var book = new XLWorkbook(new MemoryStream(bytes));
        var cell = book.Worksheet("Ho so").Cell(2, 2);
        Assert.StartsWith("'", cell.GetString());
        Assert.False(cell.HasFormula);
    }

    [Theory]
    [InlineData("=SUM(1,2)")]
    [InlineData("+SUM(1,2)")]
    [InlineData("-SUM(1,2)")]
    [InlineData("@SUM(1,2)")]
    public void Export_neutralizes_formula_like_names_after_xlsx_roundtrip(string candidateName)
    {
        var input = Fact(99, Guid.NewGuid(), 9, ApplicationStages.New, name: candidateName);
        var summary = HrReportRollup.Summary([input]);
        var row = HrReportRollup.Row(input, false);
        var data = HrRecruitmentReportExcelWriter.Write(summary, [row], "test", false, DateTime.UtcNow);
        using var reopened = new XLWorkbook(new MemoryStream(data));
        var cell = reopened.Worksheet("Ho so").Cell(2, 2);
        Assert.False(cell.HasFormula);
        Assert.StartsWith("'", cell.GetString());
        Assert.Equal("'" + candidateName, cell.GetString());
    }

    [Fact]
    public void Filter_rejects_repeated_unknown_and_invalid_dates()
    {
        IQueryCollection Query(params (string, string)[] items) =>
            new QueryCollection(items.ToDictionary(x => x.Item1, x => new StringValues(x.Item2)));
        Assert.NotNull(HrReportFilter.Parse(Query()).Filter);
        Assert.Null(HrReportFilter.Parse(Query(("runId", "0"))).Filter);
        Assert.Null(HrReportFilter.Parse(Query(("foo", "bar"))).Filter);
        Assert.Null(HrReportFilter.Parse(new QueryCollection(new Dictionary<string, StringValues>
        { ["jobId"] = new StringValues(new[] { "1", "2" }) })).Filter);
        Assert.Null(HrReportFilter.Parse(Query(("from", "2026-11-30"), ("to", "2026-11-01"))).Filter);
        Assert.True(HrReportFilter.Parse(Query()).Filter!.Anonymous);
        Assert.Equal(2_000, HrReportFilter.MaxExportRows);
    }
}
