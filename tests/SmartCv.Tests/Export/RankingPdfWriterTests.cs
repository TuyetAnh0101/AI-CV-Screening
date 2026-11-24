using System.Globalization;
using SmartCv.Api.Reports;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Export;
using UglyToad.PdfPig;

namespace SmartCv.Tests.Export;

/// <summary>
/// A31 PDF has identical scope/order/privacy as the ranking Excel export.
/// No database, Gemini call or live CV data is required.
/// </summary>
public sealed class RankingPdfWriterTests
{
    private static RankingResponse Ranking() => new()
    {
        RunId = 25, JobId = 5, JobTitle = "Business Analyst", RunStatus = RunStatuses.Completed,
        PromptVersionName = "v2-criteria", PassThreshold = 70,
        Items =
        [
            new RankingItemDto
            {
                EvaluationId = 11, ApplicationId = 101, Rank = 1,
                CandidateName = "Nguyễn Văn Riêng", FileName = "PRIVATE-CV-101.pdf",
                Status = EvaluationStatuses.Completed, KeywordScore = 87, AiScore = 90,
                FinalScore = 89.1m, ScoreSource = "Combined", FitLabel = "Good",
                Stage = ApplicationStages.Shortlisted
            },
            new RankingItemDto
            {
                EvaluationId = 12, ApplicationId = 102, Rank = 2,
                CandidateName = "Trần Thị Riêng", FileName = "PRIVATE-CV-102.pdf",
                Status = EvaluationStatuses.Completed, KeywordScore = 75, AiScore = null,
                FinalScore = 75, ScoreSource = "KeywordOnly", FitLabel = "Potential",
                Stage = ApplicationStages.New
            }
        ]
    };

    [Fact]
    public void Anonymous_pdf_is_real_document_with_ranking_metadata_and_no_names()
    {
        var ranking = Ranking();
        var query = new RankingExportQuery(ranking.JobId, "pdf", "all", "all", null, true, false, false);
        var bytes = RankingPdfWriter.Write(ranking, query.Apply(ranking.Items), new Dictionary<int, string> { [11] = "SQL" },
            query, "HR kiểm thử", new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc));
        Assert.True(bytes.Length > 1200);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        using var pdf = PdfDocument.Open(bytes);
        Assert.True(pdf.NumberOfPages >= 1);
        var text = string.Join(" ", pdf.GetPages().Select(p => p.Text));
        Assert.Contains("Business Analyst", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nguyễn Văn Riêng", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-CV-101", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-CV-102", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_EMAIL", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_scope_keeps_original_ranks_and_does_not_apply_new_sort()
    {
        var ranking = Ranking();
        var query = new RankingExportQuery(ranking.JobId, "pdf", ApplicationStages.Shortlisted, "all", null, true, false, false);
        var rows = query.Apply(ranking.Items);
        Assert.Single(rows);
        Assert.Equal(1, rows[0].Rank);
        Assert.Equal(101, rows[0].ApplicationId);
        var bytes = RankingPdfWriter.Write(ranking, rows, new Dictionary<int, string>(), query, "HR", DateTime.UtcNow);
        using var doc = PdfDocument.Open(bytes);
        Assert.True(doc.NumberOfPages > 0);
        Assert.EndsWith(".pdf", RankingPdfWriter.FileName(25, 5, DateTime.UtcNow));
    }

    [Fact]
    public void Pdf_query_is_a_valid_A31_format_and_invalid_formats_are_rejected()
    {
        var values = new Dictionary<string, string[]> { ["jobId"] = ["5"], ["format"] = ["pdf"], ["stage"] = ["Shortlisted"], ["anonymous"] = ["true"] };
        var (query, errors) = RankingExportQuery.Parse(values);
        Assert.Empty(errors);
        Assert.Equal("pdf", query!.Format);
        values["format"] = ["exe"];
        Assert.Null(RankingExportQuery.Parse(values).Query);
    }
}
