extern alias web;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Export;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Export;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Export;

public class A31ExportTests
{
    private static WebApplicationFactory<Program> Factory(int maxRows = 5000, A28TestSupport.DataFault? fault = null) =>
        A28TestSupport.Factory(new() { ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false",
            ["Export:MaxRows"] = maxRows.ToString(), ["Logging:EventLog:LogLevel:Default"] = "None" }, fault);

    internal static async Task<int> Seed(WebApplicationFactory<Program> factory, string status = "Completed")
    {
        var id = 0;
        await A28TestSupport.WithDb(factory, async db =>
        {
            Assert.Equal("Microsoft.EntityFrameworkCore.InMemory", db.Database.ProviderName);
            await db.Database.EnsureCreatedAsync();
            var job = new Job { Title = "A31 Backend .NET", CreatedAt = DateTime.UtcNow, Skills =
                [new JobSkill { SkillCode = "REQ", DisplayName = "Docker", SkillType = SkillTypes.MustHave },
                 new JobSkill { SkillCode = "OPT", DisplayName = "Optional secret skill", SkillType = SkillTypes.NiceToHave }] };
            var run = new EvaluationRun
            {
                RunType = RunTypes.Screening, Status = status, TotalItems = 4, ProcessedItems = 4, FailedItems = 1,
                CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow, PassThreshold = 70,
                ModelName = "synthetic", KeywordWeight = .3m, AiWeight = .7m,
                PromptVersion = new PromptVersion { VersionName = "A31 saved prompt", SystemPrompt = "SYNTHETIC_PRIVATE_PROMPT" }
            };
            for (var i = 0; i < 4; i++)
            {
                var row = new Evaluation
                {
                    Application = new Application { Job = job, Stage = i is 0 or 2 ? "Shortlisted" : "New", Cv = new Cv
                    {
                        CvId = Guid.NewGuid(), OriginalFileName = $"SYNTHETIC_PRIVATE_FILE_{i}.pdf", StoredPath = "not-used",
                        ContentType = "application/pdf", FileHash = i.ToString().PadLeft(64, 'a'), ExtractionStatus = "Completed",
                        ExtractedText = "SYNTHETIC_PRIVATE_CV_TEXT", Candidate = new Candidate { FullName = $"Nguyễn Văn An {i}", Email = $"cv{i}@example.invalid" }
                    } },
                    Status = i == 3 ? "Failed" : "Completed", FinalScore = i == 3 ? 999 : i == 0 ? 91.5m : 80,
                    KeywordScore = 85, AiScore = i == 2 ? null : 90, FitLabel = i == 0 ? FitLevels.Excellent : FitLevels.Good,
                    ScoreSource = i == 2 ? ScoreSources.KeywordOnly : ScoreSources.Combined, PredictedPass = true,
                    Reasoning = "SYNTHETIC_PRIVATE_REASONING", ErrorMessage = "SYNTHETIC_PRIVATE_ERROR", CreatedAt = DateTime.UtcNow
                };
                if (i == 0) row.Skills = [new EvaluationSkill { JobSkill = job.Skills.First(), IsMatched = false },
                    new EvaluationSkill { JobSkill = job.Skills.Last(), IsMatched = false }];
                run.Evaluations.Add(row);
            }
            db.EvaluationRuns.Add(run); await db.SaveChangesAsync(); id = run.RunId;
        });
        return id;
    }

    // Admin is refused ranking export since #114 (B57RoleBoundaryTests.R1); the success contract is HR's.
    [Theory]
    [InlineData("HR")]
    public async Task Real_excel_default_scope_matches_ranking_and_records_one_atomic_verified_event(string role)
    {
        using var factory = Factory(); var actor = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role);
        var id = await Seed(factory); using var http = A29AuthorizationTests.Client(factory, actor);
        var ranking = (await http.GetFromJsonAsync<RankingResponse>($"/api/runs/{id}/ranking"))!;
        var preview = (await http.GetFromJsonAsync<RankingExportPreviewDto>($"/api/runs/{id}/ranking/export/preview"))!;
        Assert.Equal((4, 2, 2, true, true), (preview.TotalCount, preview.RowCount, preview.ShortlistedCount, preview.CanExport, preview.Anonymous));
        await A28TestSupport.WithDb(factory, async db => Assert.Empty(await db.ExportLogs.ToListAsync()));
        using var response = await http.GetAsync($"/api/runs/{id}/ranking/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(ExcelRankingWriter.ContentType, response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".xlsx", response.Content.Headers.ContentDisposition!.FileNameStar);
        var bytes = await response.Content.ReadAsByteArrayAsync(); Assert.True(bytes.Length > 5000);
        using (var stream = new MemoryStream(bytes))
        using (var document = SpreadsheetDocument.Open(stream, false)) Assert.Empty(new OpenXmlValidator().Validate(document));
        using var book = new XLWorkbook(new MemoryStream(bytes)); var sheet = book.Worksheet(1);
        Assert.Equal(10, sheet.LastColumnUsed()!.ColumnNumber()); Assert.Single(sheet.Tables);
        var expected = ranking.Items.Where(r => r.Stage == "Shortlisted").ToList();
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Rank, sheet.Cell(8 + i, 1).GetValue<int>());
            Assert.Equal(RankingExportQuery.Alias(expected[i].Rank, expected[i].ApplicationId), sheet.Cell(8 + i, 2).GetString());
        }
        Assert.Equal(XLDataType.Number, sheet.Cell(8, 5).DataType); Assert.Equal(91.5m, sheet.Cell(8, 5).GetValue<decimal>());
        Assert.Equal("Docker", sheet.Cell(8, 8).GetString()); Assert.Contains("Chỉ từ khóa", sheet.Cell(9, 7).GetString());
        Assert.Contains("AI chỉ hỗ trợ, HR quyết định", sheet.Cell(5, 1).GetString());
        var text = string.Join(" ", sheet.CellsUsed().Select(c => c.GetString()));
        foreach (var secret in new[] { "Nguyễn", "SYNTHETIC_PRIVATE", "Optional secret skill", "example.invalid" }) Assert.DoesNotContain(secret, text);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var log = Assert.Single(await db.ExportLogs.Include(e => e.AuditEvent).ToListAsync());
            Assert.Equal((2, true, actor.UserId, role, "AccessGranted"),
                (log.RowCount, log.Anonymized, log.AuditEvent.ActorUserId, log.AuditEvent.ActorRoleSnapshot, log.AuditEvent.Outcome));
            Assert.Equal("runs.ranking_exported", log.AuditEvent.ActionCode);
            Assert.Single(await db.AuditEvents.Where(e => e.ActionCode == "runs.ranking_exported").ToListAsync());
            Assert.Empty(await db.ApplicationEvents.ToListAsync()); Assert.Equal(4, await db.Evaluations.CountAsync());
        });
    }

    [Fact]
    public async Task All_scope_failure_cells_are_empty_and_nonanonymous_export_preserves_names()
    {
        using var factory = Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var id = await Seed(factory); using var http = A29AuthorizationTests.Client(factory, user);
        using var response = await http.GetAsync($"/api/runs/{id}/ranking/export?stage=all&anonymous=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync())); var sheet = book.Worksheet(1);
        Assert.Equal(11, sheet.LastRowUsed()!.RowNumber()); Assert.StartsWith("Nguyễn", sheet.Cell(8, 2).GetString());
        Assert.True(sheet.Cell(11, 1).IsEmpty()); Assert.True(sheet.Cell(11, 5).IsEmpty());
        Assert.Equal("Chưa chấm xong / lỗi", sheet.Cell(11, 6).GetString());
    }

    [Fact]
    public async Task Invalid_duplicate_unknown_missing_scope_state_and_limit_return_safe_errors_without_success_logs()
    {
        using var factory = Factory(maxRows: 1); var actor = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var id = await Seed(factory); var running = await Seed(factory, "Running"); using var http = A29AuthorizationTests.Client(factory, actor);
        foreach (var query in new[] { "format=docx", "jobId=0", "jobId=abc", "stage=Hired", "fit=xyz", "anonymous=1", "search=", "search=" + new string('a', 101), "fit=all&fit=pass", "actor=forged" })
        {
            using var response = await http.GetAsync($"/api/runs/{id}/ranking/export?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("code", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"/api/runs/{id}/ranking/export")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/runs/0/ranking/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/runs/999999/ranking/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/runs/{id}/ranking/export?jobId=99999")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync($"/api/runs/{id}/ranking/export?stage=Interview")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync($"/api/runs/{running}/ranking/export")).StatusCode);
        var preview = (await http.GetFromJsonAsync<RankingExportPreviewDto>($"/api/runs/{id}/ranking/export/preview"))!;
        Assert.False(preview.CanExport); Assert.Equal("export_too_many_rows", preview.BlockCode);
        await A28TestSupport.WithDb(factory, async db => Assert.Empty(await db.ExportLogs.ToListAsync()));
    }

    [Fact]
    public async Task Anonymous_temporary_password_and_HR_log_access_are_enforced_by_JWT()
    {
        using var factory = Factory(); using var anonymous = factory.CreateClient();
        var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var temporary = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR", mustChange: true);
        using var http = A29AuthorizationTests.Client(factory, hr); using var blocked = A29AuthorizationTests.Client(factory, temporary);
        foreach (var path in new[] { "/api/exports/settings", "/api/exports", "/api/runs/1/ranking/export", "/api/runs/1/ranking/export/preview" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync(path)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/exports")).StatusCode);
        var settings = (await http.GetFromJsonAsync<ExportSettingsDto>("/api/exports/settings"))!;
        Assert.False(settings.CanViewLogs); Assert.True(settings.PdfEnabled); Assert.Contains("pdf", settings.Formats); Assert.True(settings.CanExport);
    }

    [Fact]
    public async Task Separate_concurrent_exports_are_recorded_once_each_and_Admin_logs_are_bounded_and_filtered()
    {
        // Since #114: HR exports (business), Admin reads the export journal (system); neither does the other's job.
        using var factory = Factory(); var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password());
        var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var id = await Seed(factory); using var http = A29AuthorizationTests.Client(factory, admin); using var exporter = A29AuthorizationTests.Client(factory, hr);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => exporter.GetAsync($"/api/runs/{id}/ranking/export?search=HS&stage=all")));
        foreach (var response in results) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); response.Dispose(); }
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/api/runs/{id}/ranking/export")).StatusCode);
        var page = (await http.GetFromJsonAsync<ExportLogPageDto>($"/api/exports?runId={id}&actorUserId={hr.UserId}&anonymous=true&pageSize=1"))!;
        Assert.Equal((2, 2, 1), (page.TotalCount, page.TotalPages, page.Items.Count)); Assert.Equal(hr.FullName, page.Items[0].ExportedBy);
        var next = (await http.GetFromJsonAsync<ExportLogPageDto>($"/api/exports?runId={id}&pageSize=1&page=2"))!;
        Assert.NotEqual(page.Items[0].ExportId, next.Items[0].ExportId); Assert.True(page.Items[0].SearchApplied);
        Assert.Empty((await http.GetFromJsonAsync<ExportLogPageDto>("/api/exports?runId=99999"))!.Items);
        foreach (var query in new[] { "page=0", "pageSize=101", "runId=abc", "anonymous=1", "jobId=1&jobId=2", "from=2026-01-01&to=2026-12-31" })
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/exports?" + query)).StatusCode);
        await A28TestSupport.WithDb(factory, async db => Assert.DoesNotContain("HS", JsonSerializer.Serialize(await db.ExportLogs.ToListAsync())));
    }

    [Fact]
    public async Task Failed_journal_save_never_returns_a_file_or_success_event()
    {
        var fault = new A28TestSupport.DataFault(); using var factory = Factory(fault: fault);
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR"); var id = await Seed(factory);
        using var http = A29AuthorizationTests.Client(factory, user); fault.FailSave = true;
        using var response = await http.GetAsync($"/api/runs/{id}/ranking/export");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition); Assert.DoesNotContain("SYNTHETIC_PRIVATE_SQL_ERROR", await response.Content.ReadAsStringAsync());
        fault.FailSave = false;
        await A28TestSupport.WithDb(factory, async db => { Assert.Empty(await db.ExportLogs.ToListAsync()); Assert.Empty(await db.AuditEvents.Where(e => e.Outcome == "AccessGranted").ToListAsync()); });
    }

    [Fact]
    public async Task Current_stage_changes_and_retention_anonymization_are_respected_without_changing_scores()
    {
        using var factory = Factory(); var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        var id = await Seed(factory); using var http = A29AuthorizationTests.Client(factory, user);
        var before = (await http.GetFromJsonAsync<RankingResponse>($"/api/runs/{id}/ranking"))!;
        await A28TestSupport.WithDb(factory, async db =>
        {
            var first = await db.Evaluations.Include(e => e.Application).ThenInclude(a => a.Cv).SingleAsync(e => e.EvaluationId == before.Items[0].EvaluationId);
            first.Application.Stage = "Interview"; first.Application.Cv.AnonymizedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });
        var preview = (await http.GetFromJsonAsync<RankingExportPreviewDto>($"/api/runs/{id}/ranking/export/preview"))!;
        Assert.Equal(1, preview.RowCount);
        using var response = await http.GetAsync($"/api/runs/{id}/ranking/export?stage=Interview&anonymous=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var book = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var name = book.Worksheet(1).Cell(8, 2).GetString(); Assert.DoesNotContain("Nguyễn", name); Assert.DoesNotContain("PRIVATE_FILE", name);
        var after = (await http.GetFromJsonAsync<RankingResponse>($"/api/runs/{id}/ranking"))!;
        Assert.Equal(before.Items.Select(r => r.FinalScore), after.Items.Select(r => r.FinalScore));
    }

    [Fact]
    public async Task Disabled_business_feature_hides_all_export_routes()
    {
        using var factory = A28TestSupport.Factory(new() { ["CvUpload:Endpoint:Enabled"] = "false" });
        // An HR business session: unmapped routes reach the (business) fallback policy and then 404.
        var user = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR"); using var http = A29AuthorizationTests.Client(factory, user);
        foreach (var path in new[] { "/api/exports", "/api/exports/settings", "/api/runs/1/ranking/export", "/api/runs/1/ranking/export/preview" })
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(path)).StatusCode);
    }

    [Fact]
    public void Export_filters_and_aliases_match_current_UI_and_do_not_search_hidden_identity()
    {
        var rows = new[] { new RankingItemDto { Rank = 2, ApplicationId = 101, Status = "Completed", FinalScore = 80,
            PredictedPass = true, CandidateName = "Đặng Thị Ánh", FileName = "synthetic.pdf", Stage = "New", NeedsReview = true },
            new RankingItemDto { Rank = 2, ApplicationId = 102, Stage = "Shortlisted", Status = "Completed", FinalScore = 80 } };
        var q = new RankingExportQuery(null, "xlsx", "all", "all", "anh dang", false, false, false);
        Assert.Equal(rows.Where(r => web::SmartCv.Web.Helpers.RankingView.MatchesSearch(r, q.Search)), q.Apply(rows));
        Assert.Empty((q with { Anonymous = true }).Apply(rows));
        Assert.Equal(web::SmartCv.Web.Services.AnonymityState.Alias(2, 101), RankingExportQuery.Alias(2, 101));
        Assert.NotEqual(RankingExportQuery.Alias(2, 101), RankingExportQuery.Alias(2, 102));
        Assert.Single((q with { Search = null, NeedsReview = true }).Apply(rows));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://example.invalid\",\"x\")")]
    [InlineData("+CMD|' /C calc'!A0")]
    public void Workbook_writes_untrusted_strings_as_text_without_formulas_or_external_links(string hostile)
    {
        var row = new RankingItemDto { Rank = 1, ApplicationId = 1, Status = "Completed", FinalScore = 90, CandidateName = hostile, Stage = "Shortlisted" };
        var ranking = new RankingResponse { RunId = 1, JobTitle = "Synthetic", Items = [row] };
        var bytes = ExcelRankingWriter.Write(ranking, [row], new Dictionary<int, string>(), new(null, "xlsx", "all", "all", null, false, false, false), "HR", DateTime.UtcNow);
        using var book = new XLWorkbook(new MemoryStream(bytes)); var cell = book.Worksheet(1).Cell(8, 2);
        Assert.False(cell.HasFormula); Assert.Equal(hostile, cell.GetString());
        using var zip = new ZipArchive(new MemoryStream(bytes)); Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("externalLinks"));
    }
}
