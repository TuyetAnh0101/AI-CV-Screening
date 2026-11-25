using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.E2E;

/// <summary>
/// B57 R3/R5/R6/R7/R9/R10/R12: recruitment and admin reports, opened with real parsers (ClosedXML, PdfPig, CSV)
/// and compared value by value against the synthetic graph of each workspace.
/// </summary>
public sealed class B57ReportScopeTests
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    internal static List<string> Cells(byte[] xlsx, out XLWorkbook book)
    {
        book = new XLWorkbook(new MemoryStream(xlsx));
        return book.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()).ToList();
    }

    internal static string PdfText(byte[] pdf)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(p => p.Text));
    }

    private static string Today => DateTime.UtcNow.AddHours(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(B57World.Internal)]
    [InlineData(B57World.OwnerAWorkspace)]
    [InlineData(B57World.OwnerBWorkspace)]
    public async Task R3_R6_summary_rows_preview_Excel_and_PDF_contain_exactly_the_session_workspace(int workspace)
    {
        using var world = await B57World.CreateAsync();
        using var http = world.Business(workspace);
        var mine = world.Data[workspace]; var marker = B57World.Marker(workspace);

        var summary = (await http.GetFromJsonAsync<HrReportSummaryDto>("/api/hr/reports/summary"))!;
        Assert.Equal(2, summary.Applications); Assert.Equal(2, summary.UniqueCvs); Assert.Equal(2, summary.KnownUniqueCandidates);
        Assert.Equal(2, summary.EvaluatedApplications); Assert.Equal(1, summary.PassedScore); Assert.Equal(1, summary.BelowThreshold);
        Assert.Equal((87m + workspace + 47m + workspace) / 2, summary.AverageFinalScore);

        var page = (await http.GetFromJsonAsync<HrReportPageDto>("/api/hr/reports/rows?anonymous=false&sort=appliedAt&direction=asc"))!;
        Assert.Equal(2, page.TotalRows); Assert.False(page.Anonymous);
        Assert.Equal(mine.Applications, page.Items.Select(i => i.ApplicationId).ToArray());
        Assert.Equal(new[] { $"{marker} candidate 0", $"{marker} candidate 1" }, page.Items.Select(i => i.Applicant).ToArray());
        Assert.All(page.Items, i => { Assert.Equal(mine.Job, i.JobId); Assert.Equal(mine.Run, i.RunId); Assert.Equal(70m, i.PassThreshold); });
        Assert.Equal(new decimal?[] { 87m + workspace, 47m + workspace }, page.Items.Select(i => i.FinalScore).ToArray());
        Assert.Equal(new[] { ApplicationStages.Shortlisted, ApplicationStages.Rejected }, page.Items.Select(i => i.Stage).ToArray());

        var preview = (await http.GetFromJsonAsync<HrReportPreviewDto>("/api/hr/reports/preview"))!;
        Assert.Equal(2, preview.TotalRows); Assert.True(preview.CanExport); Assert.True(preview.Anonymous);

        using var excel = await http.GetAsync("/api/hr/reports/export?anonymous=false&sort=appliedAt&direction=asc");
        Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
        Assert.Equal(Xlsx, excel.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".xlsx", excel.Content.Headers.ContentDisposition!.FileNameStar ?? excel.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        var cells = Cells(await excel.Content.ReadAsByteArrayAsync(), out var book);
        using (book)
        {
            var details = book.Worksheet("Ho so");
            Assert.Equal(3, details.LastRowUsed()!.RowNumber()); // header + 2 rows, nothing else
            Assert.Equal(mine.Applications[0], details.Cell(2, 1).GetValue<int>());
            Assert.Equal($"{marker} candidate 0", details.Cell(2, 2).GetString());
            Assert.Equal($"{marker} job", details.Cell(3, 3).GetString());
            Assert.Equal(87d + workspace, details.Cell(2, 8).GetDouble());
            Assert.Equal(2, book.Worksheet("Tong quan").Cell(6, 2).GetValue<int>());
        }
        Assert.Contains(cells, c => c.Contains(marker));
        foreach (var foreign in B57World.ForeignMarkers(workspace)) Assert.DoesNotContain(cells, c => c.Contains(foreign));
        Assert.DoesNotContain(cells, c => c.Contains("SECRET") || c.Contains("@example.invalid"));

        using var pdf = await http.GetAsync("/api/hr/reports/export/pdf?anonymous=false");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        var text = PdfText(bytes);
        Assert.Contains(marker, text);
        foreach (var foreign in B57World.ForeignMarkers(workspace)) Assert.DoesNotContain(foreign, text);
        Assert.DoesNotContain("SECRET", text);
    }

    [Fact]
    public async Task R6_R9_default_export_is_anonymous_and_ranking_Excel_is_scoped_to_the_owner()
    {
        using var world = await B57World.CreateAsync();
        using var a = world.Business(B57World.OwnerAWorkspace);
        using var excel = await a.GetAsync("/api/hr/reports/export");
        var cells = Cells(await excel.Content.ReadAsByteArrayAsync(), out var book);
        using (book)
        {
            Assert.DoesNotContain(cells, c => c.Contains("candidate 0") || c.Contains("candidate 1"));
            Assert.Equal($"HS-{world.Data[2].Applications[0]:000000}", book.Worksheet("Ho so").Cell(3, 2).GetString().Length > 0
                ? book.Worksheet("Ho so").Column(2).CellsUsed().Skip(1).Select(c => c.GetString()).Order().First() : "");
        }

        var run = world.Data[B57World.OwnerAWorkspace].Run;
        using var ranking = await a.GetAsync($"/api/runs/{run}/ranking/export?stage=all&anonymous=false");
        Assert.Equal(HttpStatusCode.OK, ranking.StatusCode);
        Assert.Equal(Xlsx, ranking.Content.Headers.ContentType!.MediaType);
        var rankingCells = Cells(await ranking.Content.ReadAsByteArrayAsync(), out var rankingBook);
        using (rankingBook)
        {
            Assert.Contains(rankingCells, c => c.Contains("B57_OWNER_A_PRIVATE candidate 0"));
            foreach (var foreign in B57World.ForeignMarkers(B57World.OwnerAWorkspace)) Assert.DoesNotContain(rankingCells, c => c.Contains(foreign));
            Assert.DoesNotContain(rankingCells, c => c.Contains("SECRET"));
        }
    }

    [Fact]
    public async Task R5_Owner_A_cannot_scope_any_report_or_export_to_Owner_B_by_id_or_tampered_filter()
    {
        using var world = await B57World.CreateAsync();
        using var a = world.Business(B57World.OwnerAWorkspace);
        var b = world.Data[B57World.OwnerBWorkspace];
        foreach (var path in new[]
                 {
                     $"/api/hr/reports/summary?jobId={b.Job}", $"/api/hr/reports/rows?runId={b.Run}", $"/api/hr/reports/preview?jobId={b.Job}",
                     $"/api/hr/reports/export?runId={b.Run}", $"/api/hr/reports/export/pdf?jobId={b.Job}",
                     $"/api/runs/{b.Run}/ranking/export", $"/api/runs/{b.Run}/ranking/export/preview", $"/api/cvs/{b.Cvs[0]}/file",
                     $"/api/candidates/{b.Candidates[0]}/data-export", $"/api/evaluations/{b.Evaluations[0]}", $"/api/runs/{b.Run}/ranking"
                 })
        {
            using var response = await a.GetAsync(path);
            Assert.True(HttpStatusCode.NotFound == response.StatusCode, $"{path}: {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("B57_OWNER_B_PRIVATE", body);
        }
        // Workspace / owner / user identifiers are never accepted as report filters.
        foreach (var query in new[] { "workspaceId=3", "ownerId=" + world.OwnerB.UserId, "userId=" + world.OwnerB.UserId, "jobId=1&jobId=2", "jobId=-1", "runId=abc" })
            Assert.Equal(HttpStatusCode.BadRequest, (await a.GetAsync("/api/hr/reports/export?" + query)).StatusCode);
        // Lists ignore smuggled workspace parameters instead of switching scope.
        var jobs = await a.GetStringAsync("/api/jobs?workspaceId=3");
        Assert.Contains("B57_OWNER_A_PRIVATE", jobs); Assert.DoesNotContain("B57_OWNER_B_PRIVATE", jobs);
        await world.WithGlobalDb(async db => Assert.DoesNotContain(db.AuditEvents, e => e.Outcome == "AccessGranted" && e.ActionCode.StartsWith("hr.recruitment_report")));
    }

    [Fact]
    public async Task R9_formula_like_candidate_and_job_text_is_stored_as_inert_text_in_Excel_and_PDF()
    {
        using var world = await B57World.CreateAsync();
        const string formula = "=HYPERLINK(\"http://example.invalid\",\"B57\")";
        await world.WithDb(B57World.OwnerAWorkspace, async db =>
        {
            (await db.Candidates.FirstAsync()).FullName = formula;
            (await db.Jobs.FirstAsync()).Title = "+SUM(1,2) B57_OWNER_A_PRIVATE";
            await db.SaveChangesAsync();
        });
        using var a = world.Business(B57World.OwnerAWorkspace);
        using var excel = await a.GetAsync("/api/hr/reports/export?anonymous=false");
        Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
        using var book = new XLWorkbook(new MemoryStream(await excel.Content.ReadAsByteArrayAsync()));
        var used = book.Worksheets.SelectMany(s => s.CellsUsed()).ToList();
        Assert.All(used, c => Assert.False(c.HasFormula, $"{c.Address} must not be a formula"));
        Assert.DoesNotContain(used, c => c.GetString().StartsWith('=') || c.GetString().StartsWith('+'));
        Assert.Contains(used, c => c.GetString().Contains("HYPERLINK"));

        using var ranking = await a.GetAsync($"/api/runs/{world.Data[2].Run}/ranking/export?stage=all&anonymous=false");
        using var rankingBook = new XLWorkbook(new MemoryStream(await ranking.Content.ReadAsByteArrayAsync()));
        Assert.All(rankingBook.Worksheets.SelectMany(s => s.CellsUsed()), c => Assert.False(c.HasFormula, $"ranking {c.Address} must not be a formula"));
    }

    [Fact]
    public async Task R10_successful_export_is_audited_in_the_owner_workspace_with_safe_metadata_and_denials_are_not_successes()
    {
        using var world = await B57World.CreateAsync();
        using var a = world.Business(B57World.OwnerAWorkspace);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/hr/reports/export?anonymous=false")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/hr/reports/export/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/hr/reports/export?jobId={world.Data[3].Job}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/admin/reports/v2/download")).StatusCode);
        using var admin = world.AdminClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/hr/reports/export")).StatusCode);
        await world.WithGlobalDb(async db =>
        {
            var events = await db.AuditEvents.AsNoTracking().Where(e => e.ActionCode.StartsWith("hr.recruitment_report") || e.ActionCode == "admin.operations_report_exported").ToListAsync();
            var granted = events.Where(e => e.Outcome == "AccessGranted").OrderBy(e => e.AuditEventId).ToList();
            Assert.Equal(new[] { "hr.recruitment_report_exported", "hr.recruitment_report_pdf_exported" }, granted.Select(e => e.ActionCode).ToArray());
            Assert.All(granted, e =>
            {
                Assert.Equal(B57World.OwnerAWorkspace, e.WorkspaceId);
                Assert.Equal(world.OwnerA.UserId, e.ActorUserId);
                Assert.Contains("2 Application", e.SafeSummary);
                Assert.DoesNotContain("B57_", e.SafeSummary);
                Assert.DoesNotContain("@", e.SafeSummary ?? "");
            });
        });
    }

    [Fact]
    public async Task R7_admin_v1_CSV_and_v2_XLSX_CSV_are_aggregate_only_and_count_every_workspace()
    {
        using var world = await B57World.CreateAsync();
        // Put each workspace's run inside the admin reporting window and give it a quota ledger row.
        var now = DateTime.UtcNow.AddMinutes(-5);
        foreach (var w in B57World.Workspaces)
            await world.WithDb(w, async db =>
            {
                var run = await db.EvaluationRuns.SingleAsync(); run.CreatedAt = now; run.Status = RunStatuses.Failed;
                db.AiUsageEntries.Add(new SmartCv.Core.Entities.AiUsageEntry { RunId = run.RunId, Source = "Screening", Units = 2, CreatedAtUtc = now });
                await db.SaveChangesAsync();
            });
        using var admin = world.AdminClient();
        var range = $"from={DateTime.UtcNow.AddHours(7).AddDays(-6):yyyy-MM-dd}&to={Today}";
        var summary = await admin.GetStringAsync("/api/admin/reports/summary?" + range);
        using (var json = JsonDocument.Parse(summary))
        {
            var root = json.RootElement;
            Assert.Equal(4, root.GetProperty("workspaces").GetProperty("totalNow").GetInt64());
            Assert.Equal(6, root.GetProperty("ai").GetProperty("screeningUnits").GetInt64());
            Assert.Equal(3, root.GetProperty("ai").GetProperty("failedRuns").GetInt64());
        }
        var payloads = new List<string> { summary, await admin.GetStringAsync("/api/admin/reports/trend?" + range) };
        using (var csv = await admin.GetAsync("/api/admin/reports/download?" + range))
        {
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
            payloads.Add(Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync()));
        }
        foreach (var format in new[] { "xlsx", "csv" })
        {
            using var response = await admin.GetAsync($"/api/admin/reports/v2/download?report=all&format={format}&{range}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.EndsWith("." + format, response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
            if (format == "xlsx")
            {
                var cells = Cells(bytes, out var book);
                using (book) Assert.All(book.Worksheets.SelectMany(s => s.CellsUsed()), c => Assert.False(c.HasFormula));
                payloads.Add(string.Join("\n", cells));
            }
            else
            {
                var text = Encoding.UTF8.GetString(bytes);
                foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                    foreach (var cell in line.Split("\",\"").Select(c => c.Trim('"', '﻿')))
                        Assert.False(cell.Length > 0 && "=+@".Contains(cell[0]), "CSV cell may be interpreted as formula: " + cell);
                payloads.Add(text);
            }
        }
        foreach (var payload in payloads)
        {
            Assert.DoesNotContain("B57_", payload);
            Assert.DoesNotContain("@example.invalid", payload);
            Assert.DoesNotContain("AQAAAA", payload);
            Assert.DoesNotContain(A28TestSupport.Key, payload);
        }
        await world.WithGlobalDb(async db =>
        {
            var audits = await db.AuditEvents.AsNoTracking().Where(e => e.ActionCode == "admin.operations_report_exported").ToListAsync();
            Assert.Equal(3, audits.Count);
            Assert.All(audits, e => { Assert.Equal("AccessGranted", e.Outcome); Assert.Equal(world.Admin.UserId, e.ActorUserId); Assert.Contains("metadata", e.SafeSummary); });
        });
    }

    [Fact]
    public async Task R12_empty_workspace_reports_real_zeroes_and_a_valid_empty_file_without_fabricated_scores()
    {
        using var world = await B57World.CreateAsync();
        using var empty = world.Business(B57World.EmptyWorkspace);
        var summary = (await empty.GetFromJsonAsync<HrReportSummaryDto>("/api/hr/reports/summary"))!;
        Assert.Equal(0, summary.Applications); Assert.Equal(0, summary.EvaluatedApplications);
        Assert.Null(summary.AverageFinalScore);
        var rows = (await empty.GetFromJsonAsync<HrReportPageDto>("/api/hr/reports/rows"))!;
        Assert.Equal(0, rows.TotalRows); Assert.Empty(rows.Items);
        using var excel = await empty.GetAsync("/api/hr/reports/export");
        Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
        var cells = Cells(await excel.Content.ReadAsByteArrayAsync(), out var book);
        using (book) Assert.Equal(1, book.Worksheet("Ho so").LastRowUsed()!.RowNumber());
        foreach (var w in B57World.Workspaces) Assert.DoesNotContain(cells, c => c.Contains(B57World.Marker(w)));
        using var pdf = await empty.GetAsync("/api/hr/reports/export/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.DoesNotContain("B57_", PdfText(await pdf.Content.ReadAsByteArrayAsync()));
        var dashboard = await empty.GetStringAsync("/api/dashboard/summary");
        using var json = JsonDocument.Parse(dashboard);
        Assert.Equal(0, json.RootElement.GetProperty("totalApplications").GetInt32());
        Assert.DoesNotContain("B57_", dashboard);
    }

    [Theory]
    [InlineData("from=2026-11-20&to=2026-11-01", HttpStatusCode.BadRequest)]
    [InlineData("from=2026-13-01", HttpStatusCode.BadRequest)]
    [InlineData("stage=Hacked", HttpStatusCode.BadRequest)]
    [InlineData("evaluation=all%27--", HttpStatusCode.BadRequest)]
    [InlineData("pageSize=100000", HttpStatusCode.BadRequest)]
    [InlineData("jobId=2147483647", HttpStatusCode.NotFound)]
    [InlineData("runId=2147483647", HttpStatusCode.NotFound)]
    [InlineData("stage=Interview", HttpStatusCode.OK)]
    public async Task R12_invalid_missing_or_out_of_range_filters_are_explicit_client_errors(string query, HttpStatusCode expected)
    {
        using var world = await B57World.CreateAsync();
        using var a = world.Business(B57World.OwnerAWorkspace);
        foreach (var path in new[] { "/api/hr/reports/summary", "/api/hr/reports/rows", "/api/hr/reports/preview", "/api/hr/reports/export", "/api/hr/reports/export/pdf" })
        {
            using var response = await a.GetAsync(path + "?" + query);
            Assert.True(expected == response.StatusCode, $"{path}?{query}: {(int)response.StatusCode}");
            if (expected != HttpStatusCode.OK)
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
                Assert.DoesNotContain("B57_", body); Assert.DoesNotContain("Exception", body);
            }
        }
    }

    [Fact]
    public async Task R12_database_failure_during_report_is_a_controlled_503_and_no_export_is_audited()
    {
        using var world = await B57World.CreateAsync(faultReports: true);
        using var a = world.Business(B57World.OwnerAWorkspace);
        world.ReportFault!.Active = true; // session validation still reads the database; report queries fail
        foreach (var path in new[] { "/api/hr/reports/summary", "/api/hr/reports/rows", "/api/hr/reports/preview", "/api/hr/reports/export", "/api/hr/reports/export/pdf" })
        {
            using var response = await a.GetAsync(path);
            Assert.True(HttpStatusCode.ServiceUnavailable == response.StatusCode, $"{path}: {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("hr_report_unavailable", body);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE_SQL_ERROR", body);
            Assert.DoesNotContain("B57_", body);
        }
        world.ReportFault.Active = false;
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/hr/reports/summary")).StatusCode); // transient: recovers
        await world.WithGlobalDb(async db =>
        {
            var events = await db.AuditEvents.AsNoTracking().Where(e => e.ActionCode.StartsWith("hr.recruitment_report")).ToListAsync();
            Assert.DoesNotContain(events, e => e.Outcome == "AccessGranted" || e.Outcome == "Succeeded");
        });
    }
}

/// <summary>
/// Synthetic transient database failure for queries issued by an already authenticated /api/hr/reports request.
/// Authentication (DB session validation) runs before HttpContext.User is assigned, so it is never failed.
/// A failed compilation is never cached, therefore every report request fails while Active.
/// </summary>
internal sealed class B57ReportFault : Microsoft.EntityFrameworkCore.Diagnostics.IQueryExpressionInterceptor
{
    private static readonly Microsoft.AspNetCore.Http.HttpContextAccessor Accessor = new();
    internal volatile bool Active;
    public System.Linq.Expressions.Expression QueryCompilationStarting(System.Linq.Expressions.Expression queryExpression,
        Microsoft.EntityFrameworkCore.Diagnostics.QueryExpressionEventData eventData)
    {
        var http = Accessor.HttpContext;
        if (Active && http?.User.Identity?.IsAuthenticated == true && http.Request.Path.StartsWithSegments("/api/hr/reports"))
            throw new A28TestSupport.SyntheticDatabaseException();
        return queryExpression;
    }
}
