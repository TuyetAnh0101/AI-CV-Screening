using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Extraction;
using SmartCv.Infrastructure.Scoring;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Tests.E2E;

/// <summary>
/// B57 E2/E9: a brand-new Personal workspace goes through the real API: job + skill, upload of 3 synthetic text PDFs,
/// real text extraction, assignment, a Screening run scored by the FAKE AI evaluator (Gemini:UseFake), results,
/// stage change and the ranking Excel export opened with ClosedXML. Workers are driven exactly as the hosted
/// workers do it (per-record WorkspaceScope), so no data from other workspaces can enter the flow.
/// </summary>
public sealed class B57CvFlowTests
{
    internal static byte[] TextPdf(string line)
    {
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        var content = $"BT /F1 12 Tf 20 350 Td ({line}) Tj ET";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}\nendstream" };
        for (var i = 0; i < objects.Length; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = text.Length; text.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) text.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    [Fact]
    public async Task E2_E9_new_workspace_upload_extract_assign_score_results_stage_and_Excel_export()
    {
        using var world = await B57World.CreateAsync();
        // EF InMemory has no ExecuteUpdate: give the workspace the active prompt WorkspaceProvisioner creates at sign-up.
        await world.WithDb(B57World.EmptyWorkspace, async db =>
        {
            db.PromptVersions.Add(new SmartCv.Core.Entities.PromptVersion { VersionName = "B57 E2 prompt", SystemPrompt = "Synthetic B57 evaluation rules.", IsActive = true, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        await RunFlowAsync(world, activatePromptViaApi: false);
    }

    internal static async Task RunFlowAsync(B57World world, bool activatePromptViaApi)
    {
        const int w = B57World.EmptyWorkspace;
        using var http = world.Business(w);

        // Job + must-have skill through the public API.
        using var created = await http.PostAsJsonAsync("/api/jobs", new { title = "B57 E2 Backend Developer", minYearsExperience = 0 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("jobId").GetInt32();
        using (var skill = await http.PostAsJsonAsync($"/api/jobs/{jobId}/skills", new { skillCode = "DOCKER", displayName = "Docker", skillType = "MustHave" }))
            Assert.True(skill.IsSuccessStatusCode, await skill.Content.ReadAsStringAsync());
        if (activatePromptViaApi)
        using (var prompt = await http.PostAsJsonAsync("/api/prompts", new { versionName = "B57 E2 prompt", systemPrompt = "Synthetic B57 evaluation rules." }))
        {
            Assert.Equal(HttpStatusCode.Created, prompt.StatusCode);
            var promptId = JsonDocument.Parse(await prompt.Content.ReadAsStringAsync()).RootElement.GetProperty("promptVersionId").GetInt32();
            Assert.True((await http.PostAsync($"/api/prompts/{promptId}/activate", null)).IsSuccessStatusCode);
        }

        // Upload 3 synthetic CVs in one request.
        using var form = new MultipartFormDataContent { { new StringContent("true"), "consentConfirmed" }, { new StringContent("Email"), "sourceChannel" } };
        var lines = new[] { "B57 Synthetic One - Docker Kubernetes C# five years", "B57 Synthetic Two - Docker SQL Server three years", "B57 Synthetic Three - Excel Word no backend" };
        for (var i = 0; i < 3; i++)
            form.Add(new ByteArrayContent(TextPdf(lines[i] + " " + new string('x', 40))) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }, "files", $"b57-e2-{i}.pdf");
        using var upload = await http.PostAsync("/api/cvs/upload", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var files = JsonSerializer.Deserialize<CvUploadResponse>(await upload.Content.ReadAsStringAsync(), JsonSerializerOptions.Web)!.Files;
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.Equal(CvUploadStatuses.Accepted, f.Status));
        var cvIds = files.Select(f => f.CvId!.Value).ToList();

        // Extraction exactly like CvExtractionWorker (scope per CV workspace).
        foreach (var id in cvIds)
        {
            using var scope = world.Factory.Services.CreateScope();
            using var workspace = WorkspaceScope.Begin(w);
            await scope.ServiceProvider.GetRequiredService<CvExtractionProcessor>().ProcessAsync(id, default);
        }
        foreach (var id in cvIds)
        {
            var detail = JsonDocument.Parse(await http.GetStringAsync($"/api/cvs/{id}")).RootElement;
            Assert.Equal("Completed", detail.GetProperty("extractionStatus").GetString());
        }

        using (var assign = await http.PostAsJsonAsync("/api/applications", new CreateApplicationsRequest { JobId = jobId, CvIds = cvIds }))
            Assert.True(assign.IsSuccessStatusCode, await assign.Content.ReadAsStringAsync());
        var apps = (await http.GetFromJsonAsync<List<ApplicationDto>>($"/api/applications?jobId={jobId}"))!;
        Assert.Equal(3, apps.Count);

        using var runResponse = await http.PostAsJsonAsync("/api/runs", new { applicationIds = apps.Select(a => a.ApplicationId).ToArray() });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var runId = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetInt32();
        using (var scope = world.Factory.Services.CreateScope())
        using (WorkspaceScope.Begin(w))
            await scope.ServiceProvider.GetRequiredService<RunScoringProcessor>().ProcessRunAsync(runId, default);

        var run = JsonDocument.Parse(await http.GetStringAsync($"/api/runs/{runId}")).RootElement;
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal(3, run.GetProperty("processedItems").GetInt32());
        var ranking = await http.GetStringAsync($"/api/runs/{runId}/ranking");
        foreach (var marker in B57World.Workspaces.Select(B57World.Marker)) Assert.DoesNotContain(marker, ranking);
        using var rankingJson = JsonDocument.Parse(ranking);

        // HR decision on the first application, then the run is exported and opened.
        using (var stage = await http.PatchAsJsonAsync($"/api/applications/{apps[0].ApplicationId}/stage", new { toStage = "Shortlisted" }))
            Assert.True(stage.IsSuccessStatusCode, await stage.Content.ReadAsStringAsync());
        using var export = await http.GetAsync($"/api/runs/{runId}/ranking/export?stage=all&anonymous=false");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var book = new XLWorkbook(new MemoryStream(await export.Content.ReadAsByteArrayAsync()));
        var cells = book.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()).ToList();
        Assert.Contains(cells, c => c.Contains("B57 E2 Backend Developer"));
        foreach (var marker in B57World.Workspaces.Select(B57World.Marker)) Assert.DoesNotContain(cells, c => c.Contains(marker));
        var sheet = book.Worksheets.First();
        var scoreCells = sheet.CellsUsed().Where(c => c.DataType == XLDataType.Number && c.GetDouble() is > 0 and <= 100).ToList();
        Assert.True(scoreCells.Count >= 3, "Expected the 3 scored applications with numeric scores in the export.");

        // The same run is invisible to another owner and its numbers are in this workspace's report only.
        using var other = world.Business(B57World.OwnerAWorkspace);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/runs/{runId}/ranking/export")).StatusCode);
        var summary = (await http.GetFromJsonAsync<HrReportSummaryDto>("/api/hr/reports/summary"))!;
        Assert.Equal(3, summary.Applications); Assert.Equal(3, summary.EvaluatedApplications);
        var otherSummary = (await other.GetFromJsonAsync<HrReportSummaryDto>("/api/hr/reports/summary"))!;
        Assert.Equal(2, otherSummary.Applications);
        await world.WithGlobalDb(async db => Assert.Contains(await db.AuditEvents.Where(e => e.ActionCode == "runs.ranking_exported").ToListAsync(),
            e => e.WorkspaceId == w && e.Outcome == "AccessGranted"));
    }
}
