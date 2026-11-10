using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

public class EvaluationSetEndpointTests
{
    private static WebApplicationFactory<Program> Factory(string environment = "Development", bool enabled = true) =>
        TestSupport.ApiFactory(environment, new()
        {
            ["CvUpload:Endpoint:Enabled"] = enabled.ToString(), ["Scoring:WorkerEnabled"] = "false"
        }).WithWebHostBuilder(host => host.ConfigureLogging(logging => logging.ClearProviders()));

    private sealed record Seed(int SetId, int GoodId, int AmbiguousId, int OtherJobId, int FailedId, int JobId);

    private static async Task<Seed> SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        var jobs = await db.Jobs.OrderBy(j => j.JobId).Take(2).ToListAsync();
        Cv Cv(string name, char hash, string status = "Completed") => new()
        {
            CvId = Guid.NewGuid(), OriginalFileName = name, StoredPath = "synthetic", ContentType = "application/pdf",
            FileHash = new string(hash, 64), FileSizeBytes = 50, ExtractionStatus = status
        };
        var shared = Cv("shared.pdf", 'B');
        var good = new Application { Cv = Cv("đánh-giá.pdf", 'A'), JobId = jobs[0].JobId };
        good.Cv.Candidate = new Candidate { FullName = "Ứng viên thử nghiệm" };
        var ambiguous = new Application { Cv = shared, JobId = jobs[0].JobId };
        var other = new Application { Cv = shared, JobId = jobs[1].JobId };
        var failed = new Application { Cv = Cv("failed.pdf", 'C', "Failed"), JobId = jobs[0].JobId };
        var set = new EvaluationSet { Name = "Bộ đánh giá", Description = "Dữ liệu tổng hợp", CreatedAt = DateTime.UtcNow };
        db.AddRange(set, good, ambiguous, other, failed);
        await db.SaveChangesAsync();
        return new Seed(set.EvaluationSetId, good.ApplicationId, ambiguous.ApplicationId, other.ApplicationId,
            failed.ApplicationId, jobs[0].JobId);
    }

    [Fact]
    public async Task Create_list_detail_and_duplicate_name_behave_consistently()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var old = await SeedAsync(factory.Services);
        using var response = await client.PostAsJsonAsync("/api/evaluation-sets", new CreateEvaluationSetRequest
        {
            Name = "  Bộ mới  ", Description = "  Tiếng Việt  "
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<EvaluationSetDto>())!;
        Assert.Equal("Bộ mới", created.Name);
        Assert.Equal("Tiếng Việt", created.Description);
        Assert.Equal(0, created.LabelCount);
        Assert.Equal($"/api/evaluation-sets/{created.EvaluationSetId}", response.Headers.Location!.OriginalString);
        var list = (await client.GetFromJsonAsync<List<EvaluationSetDto>>("/api/evaluation-sets"))!;
        Assert.Equal(new[] { created.EvaluationSetId, old.SetId }, list.Select(s => s.EvaluationSetId));
        var detail = (await client.GetFromJsonAsync<EvaluationSetDetailDto>(response.Headers.Location))!;
        Assert.Empty(detail.Labels);
        Assert.Equal(DateTimeKind.Utc, detail.CreatedAt.Kind);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/evaluation-sets",
            new CreateEvaluationSetRequest { Name = "Bộ mới" })).StatusCode);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(151, 0)]
    [InlineData(1, 501)]
    public async Task Invalid_set_metadata_is_rejected_before_write(int nameLength, int descriptionLength)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/evaluation-sets", new CreateEvaluationSetRequest
        {
            Name = new string('n', nameLength), Description = new string('d', descriptionLength)
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<EvaluationSetDto>>("/api/evaluation-sets"))!);
    }

    [Theory]
    [InlineData("/api/evaluation-sets/99999")]
    [InlineData("/api/evaluation-sets/99999/applications")]
    [InlineData("/api/evaluation-sets/99999/labels/template")]
    public async Task Missing_set_returns_404(string path)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Save_reports_added_updated_unchanged_and_preserves_original_unchanged_metadata()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        var initial = new SaveHrLabelsRequest
        {
            LabeledBy = " HR ban đầu ", Labels =
            [new() { ApplicationId = seed.GoodId, IsPass = true, Note = " Đạt kỹ năng " },
             new() { ApplicationId = seed.AmbiguousId, IsPass = false }]
        };
        using var first = await client.PutAsJsonAsync($"/api/evaluation-sets/{seed.SetId}/labels", initial);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstResult = (await first.Content.ReadFromJsonAsync<SaveHrLabelsResult>())!;
        Assert.Equal((2, 0, 0, 2, 1), (firstResult.Added, firstResult.Updated, firstResult.Unchanged,
            firstResult.LabelCount, firstResult.PassCount));
        Assert.Empty(firstResult.RecalculatedRunIds);
        var before = (await client.GetFromJsonAsync<EvaluationSetDetailDto>($"/api/evaluation-sets/{seed.SetId}"))!;
        var savedGood = before.Labels.Single(l => l.ApplicationId == seed.GoodId);
        Assert.Equal("Ứng viên thử nghiệm", savedGood.CandidateName);
        Assert.Equal("đánh-giá.pdf", savedGood.FileName);
        Assert.Equal("HR ban đầu", savedGood.LabeledBy);
        Assert.Equal("Đạt kỹ năng", savedGood.Note);
        Assert.Equal(DateTimeKind.Utc, savedGood.LabeledAt.Kind);
        using var second = await client.PutAsJsonAsync($"/api/evaluation-sets/{seed.SetId}/labels", new SaveHrLabelsRequest
        {
            LabeledBy = "HR mới", Labels =
            [new() { ApplicationId = seed.GoodId, IsPass = true, Note = "Đạt kỹ năng" },
             new() { ApplicationId = seed.AmbiguousId, IsPass = true, Note = "Đổi kết luận" }]
        });
        var secondResult = (await second.Content.ReadFromJsonAsync<SaveHrLabelsResult>())!;
        Assert.Equal((0, 1, 1, 2), (secondResult.Added, secondResult.Updated, secondResult.Unchanged, secondResult.PassCount));
        var after = (await client.GetFromJsonAsync<EvaluationSetDetailDto>($"/api/evaluation-sets/{seed.SetId}"))!;
        Assert.Equal(savedGood.LabeledAt, after.Labels.Single(l => l.ApplicationId == seed.GoodId).LabeledAt);
        Assert.Equal(savedGood.LabeledBy, after.Labels.Single(l => l.ApplicationId == seed.GoodId).LabeledBy);
        var candidates = (await client.GetFromJsonAsync<List<LabelCandidateDto>>(
            $"/api/evaluation-sets/{seed.SetId}/applications?jobId={seed.JobId}"))!;
        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(seed.JobId, c.JobId));
        Assert.True(candidates.Single(c => c.ApplicationId == seed.GoodId).CurrentLabel);
    }

    [Theory]
    [InlineData("missing-person")]
    [InlineData("long-person")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("long-note")]
    [InlineData("too-many")]
    [InlineData("null-item")]
    public async Task Invalid_label_batches_never_save_a_valid_prefix(string kind)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        var labels = new List<HrLabelInput> { new() { ApplicationId = seed.GoodId, IsPass = true } };
        var by = kind == "missing-person" ? " " : kind == "long-person" ? new string('h', 101) : "HR";
        if (kind == "empty") labels.Clear();
        if (kind == "unknown") labels.Add(new() { ApplicationId = 99999, IsPass = false });
        if (kind == "duplicate") labels.Add(new() { ApplicationId = seed.GoodId, IsPass = false });
        if (kind == "long-note") labels.Add(new() { ApplicationId = seed.AmbiguousId, Note = new string('n', 501) });
        if (kind == "too-many") labels = Enumerable.Range(1, 2001).Select(id => new HrLabelInput { ApplicationId = id }).ToList();
        if (kind == "null-item") labels.Add(null!);
        using var response = await client.PutAsJsonAsync($"/api/evaluation-sets/{seed.SetId}/labels",
            new SaveHrLabelsRequest { LabeledBy = by, Labels = labels });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<EvaluationSetDetailDto>($"/api/evaluation-sets/{seed.SetId}"))!.LabelCount);
    }

    [Fact]
    public async Task Missing_json_ispass_never_defaults_to_fail_or_saves_the_valid_prefix()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            db.Add(new HrLabel
            {
                EvaluationSetId = seed.SetId, ApplicationId = seed.GoodId,
                IsPass = false, Note = "Giữ nguyên nhãn", LabeledBy = "HR ban đầu", LabeledAt = DateTime.UtcNow
            });
            db.Add(new EvaluationRun
            {
                RunType = "PromptTest", EvaluationSetId = seed.SetId, Status = "Completed", AccuracyPercent = 80m,
                PromptVersion = new PromptVersion { VersionName = "historical", SystemPrompt = "saved" },
                ModelName = "saved-model", KeywordWeight = .3m, AiWeight = .7m
            });
            await db.SaveChangesAsync();
        }
        using var response = await client.PutAsJsonAsync($"/api/evaluation-sets/{seed.SetId}/labels", new
        {
            labeledBy = "HR mới",
            labels = new object[]
            {
                new { applicationId = seed.GoodId, isPass = true },
                new { applicationId = seed.AmbiguousId }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var check = factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var unchanged = await checkDb.HrLabels.SingleAsync();
        Assert.False(unchanged.IsPass);
        Assert.Equal("Giữ nguyên nhãn", unchanged.Note);
        Assert.Equal("HR ban đầu", unchanged.LabeledBy);
        Assert.Equal(80m, (await checkDb.EvaluationRuns.SingleAsync()).AccuracyPercent);
    }

    [Theory]
    [InlineData(",", false)]
    [InlineData(";", false)]
    [InlineData(";", true)]
    public async Task Csv_utf8_and_bom_import_valid_rows_by_id_or_unique_filename(string separator, bool bom)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        var csv = $"Mã hồ sơ{separator}Tên file{separator}Kết quả{separator}Ghi chú\r\n" +
                  $"{seed.AmbiguousId}{separator}{separator}Không đạt{separator}Cần bổ sung\r\n" +
                  $"{separator}đánh-giá.pdf{separator}Đạt{separator}Nền tảng tốt\r\n";
        using var response = await ImportAsync(client, seed.SetId, (bom ? "\uFEFF" : "") + csv);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<SaveHrLabelsResult>())!;
        Assert.Equal((2, 1), (result.Added, result.PassCount));
        var detail = (await client.GetFromJsonAsync<EvaluationSetDetailDto>($"/api/evaluation-sets/{seed.SetId}"))!;
        Assert.Equal("Nền tảng tốt", detail.Labels.Single(l => l.ApplicationId == seed.GoodId).Note);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("label")]
    [InlineData("duplicate-id")]
    [InlineData("ambiguous-file")]
    [InlineData("resolved-duplicate")]
    [InlineData("unknown-id")]
    [InlineData("unknown-file")]
    public async Task Csv_errors_report_line_and_do_not_save_any_rows(string kind)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        var invalid = kind switch
        {
            "label" => $"{seed.AmbiguousId},,Có lẽ",
            "duplicate-id" => $"{seed.GoodId},,fail",
            "ambiguous-file" => ",shared.pdf,fail",
            "resolved-duplicate" => ",đánh-giá.pdf,fail",
            "unknown-id" => "99999,,fail",
            "unknown-file" => ",unknown.pdf,fail",
            _ => ""
        };
        var csv = kind == "header" ? "ApplicationId,Note\n1,a" :
            $"ApplicationId,TenFile,KetQua\n{seed.GoodId},,pass\n{invalid}\n";
        using var response = await ImportAsync(client, seed.SetId, csv);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (kind != "header") Assert.Contains("Dòng 3", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, (await client.GetFromJsonAsync<EvaluationSetDetailDto>($"/api/evaluation-sets/{seed.SetId}"))!.LabelCount);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("extension")]
    [InlineData("size")]
    [InlineData("encoding")]
    public async Task Csv_file_validation_rejects_bad_input(string kind)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        using var form = new MultipartFormDataContent();
        var data = kind switch
        {
            "empty" => Array.Empty<byte>(), "size" => new byte[1024 * 1024 + 1],
            "encoding" => new byte[] { 0xff, 0xfe, 0x61, 0x00 },
            _ => Encoding.UTF8.GetBytes("ApplicationId,KetQua\n1,pass")
        };
        form.Add(new ByteArrayContent(data), "file", kind == "extension" ? "labels.txt" : "labels.csv");
        form.Add(new StringContent("HR"), "labeledBy");
        using var response = await client.PostAsync($"/api/evaluation-sets/{seed.SetId}/labels/import", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Template_is_utf8_excel_csv_excludes_failed_and_keeps_existing_data()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        using var response = await client.GetAsync($"/api/evaluation-sets/{seed.SetId}/labels/template?jobId={seed.JobId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes.Take(3));
        var csv = Encoding.UTF8.GetString(bytes);
        Assert.Contains("ApplicationId;TenFile;UngVien;ViTri;KetQua;GhiChu", csv);
        Assert.Contains("Ứng viên thử nghiệm", csv);
        Assert.DoesNotContain("failed.pdf", csv);
        Assert.DoesNotContain($"\n{seed.OtherJobId};", csv);
        Assert.Equal(0, (await client.GetFromJsonAsync<EvaluationSetDetailDto>($"/api/evaluation-sets/{seed.SetId}"))!.LabelCount);
    }

    [Fact]
    public async Task Changing_or_deleting_labels_invalidates_stale_accuracy_without_touching_scores_or_applications()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        int runId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var run = new EvaluationRun
            {
                RunType = "PromptTest", EvaluationSetId = seed.SetId, Status = "Completed", AccuracyPercent = 80m,
                PromptVersion = new PromptVersion { VersionName = "historical", SystemPrompt = "saved", IsActive = true },
                ModelName = "saved-model", KeywordWeight = .3m, AiWeight = .7m,
                Evaluations = [new() { ApplicationId = seed.GoodId, Status = "Completed", FinalScore = 78m, AiRawResponse = "saved-json" }]
            };
            db.Add(run);
            await db.SaveChangesAsync();
            runId = run.RunId;
        }
        using var saved = await client.PutAsJsonAsync($"/api/evaluation-sets/{seed.SetId}/labels", new SaveHrLabelsRequest
        {
            LabeledBy = "HR", Labels = [new() { ApplicationId = seed.GoodId, IsPass = true }]
        });
        var result = (await saved.Content.ReadFromJsonAsync<SaveHrLabelsResult>())!;
        Assert.Equal(new[] { runId }, result.InvalidatedRunIds);
        Assert.Empty(result.RecalculatedRunIds);
        Assert.Contains(result.Warnings, w => w.Contains("A16"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var run = await db.EvaluationRuns.SingleAsync();
            Assert.Null(run.AccuracyPercent);
            run.AccuracyPercent = 75m; // Simulates a previously persisted A16 value before another HR change.
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/evaluation-sets/{seed.SetId}/labels/{seed.GoodId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/evaluation-sets/{seed.SetId}/labels/{seed.GoodId}")).StatusCode);
        using var check = factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.Null((await checkDb.EvaluationRuns.SingleAsync()).AccuracyPercent);
        Assert.Equal(4, await checkDb.Applications.CountAsync());
        Assert.Equal(3, await checkDb.Cvs.CountAsync());
        Assert.Equal(78m, (await checkDb.Evaluations.SingleAsync()).FinalScore);
        Assert.Equal("saved-json", (await checkDb.Evaluations.SingleAsync()).AiRawResponse);
        Assert.True((await checkDb.PromptVersions.SingleAsync()).IsActive);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", false)]
    public async Task Endpoints_keep_the_existing_development_access_boundary(string environment, bool enabled)
    {
        using var factory = Factory(environment, enabled);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/evaluation-sets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/evaluation-sets", new CreateEvaluationSetRequest { Name = "test" })).StatusCode);
    }

    [Fact]
    public void Csv_parts_are_bounded_without_overriding_the_existing_global_request_limit()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var endpoint = Assert.Single(factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>(), e => e.RoutePattern.RawText == "/api/evaluation-sets/{id:int}/labels/import");
        Assert.Null(endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>());
        Assert.Equal(1024 * 1024 + 64 * 1024,
            endpoint.Metadata.GetMetadata<IFormOptionsMetadata>()!.MultipartBodyLengthLimit);
    }

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, int setId, string csv)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "labels.csv");
        form.Add(new StringContent("HR - Kiểm thử"), "labeledBy");
        return await client.PostAsync($"/api/evaluation-sets/{setId}/labels/import", form);
    }
}
