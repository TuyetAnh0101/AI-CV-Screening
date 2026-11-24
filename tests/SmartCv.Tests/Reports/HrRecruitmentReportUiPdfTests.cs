using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using SmartCv.Api.Reports;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using UglyToad.PdfPig;

namespace SmartCv.Tests.Reports;

public sealed class HrRecruitmentReportUiPdfTests
{
    private static HrReportFact Fact(int id, string job, decimal? score, string name = "Nguyễn Văn Thử") =>
        new(new HrApplicationFact(
                id, Guid.NewGuid(), id, name, id + 10, job,
                new DateTime(2026, 11, id, 2, 0, 0, DateTimeKind.Utc),
                ApplicationStages.New, null, null, false, false),
            score is null ? null : new HrScoreFact(id, 200 + id, EvaluationStatuses.Completed,
                ScoreSources.Combined, score, 70m, "Good"));

    [Fact]
    public void Sorting_is_global_deterministic_and_stable_before_pagination()
    {
        var input = new[] { Fact(3, "Java", 95), Fact(1, "Data", 67), Fact(2, "Backend", 80) };
        Assert.Equal([3, 2, 1], HrReportSort.Order(input, "score", true).Select(x => x.Application.ApplicationId));
        Assert.Equal([2, 1, 3], HrReportSort.Order(input, "job", false).Select(x => x.Application.ApplicationId));
        Assert.Equal([1, 2, 3], HrReportSort.Order(input, "applicationId", false).Select(x => x.Application.ApplicationId));
    }

    [Fact]
    public void Sort_query_is_allowlisted_and_fails_closed()
    {
        static IQueryCollection Query(string key, string value) =>
            new QueryCollection(new Dictionary<string, StringValues> { [key] = new StringValues(value) });
        Assert.Equal("appliedAt", HrReportFilter.Parse(new QueryCollection(new Dictionary<string, StringValues>())).Filter!.Sort);
        Assert.Equal("score", HrReportFilter.Parse(Query("sort", "score")).Filter!.Sort);
        Assert.Null(HrReportFilter.Parse(Query("sort", "sql;DROP TABLE Cvs")).Filter);
        Assert.Null(HrReportFilter.Parse(Query("direction", "sideways")).Filter);
        Assert.Equal("asc", HrReportFilter.Parse(Query("direction", "asc")).Filter!.Direction);
    }

    [Fact]
    public void Pdf_contains_pages_and_anonymous_application_ids_not_candidate_names()
    {
        var data = new[] { Fact(1, "Kỹ sư dữ liệu", 72, "Nguyễn Văn Riêng") };
        var summary = HrReportRollup.Summary(data);
        var anon = data.Select(r => HrReportRollup.Row(r, true)).ToArray();
        Assert.Equal("HS-000001", anon[0].Applicant);
        var bytes = HrRecruitmentReportPdfWriter.Write(summary, anon, "Job: Data; Stage: New", true,
            "Nhân viên HR", DateTime.UtcNow);
        Assert.True(bytes.Length > 500);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes.AsSpan(0, 5)));
        using var document = PdfDocument.Open(bytes);
        Assert.True(document.NumberOfPages >= 1);
        var extracted = string.Join("\n", document.GetPages().Select(page => page.Text));
        Assert.DoesNotContain("Nguyễn Văn Riêng", extracted);
    }

    [Fact]
    public void Pdf_requires_actor_and_honours_max_rows()
    {
        var rows = Array.Empty<HrReportRowDto>();
        var summary = HrReportRollup.Summary(Array.Empty<HrReportFact>());
        Assert.Throws<ArgumentException>(() => HrRecruitmentReportPdfWriter.Write(summary, rows, "all", true, "", DateTime.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => HrRecruitmentReportPdfWriter.Write(summary,
            Enumerable.Repeat(new HrReportRowDto(), 2_001).ToArray(), "all", true, "HR", DateTime.UtcNow));
    }
}
