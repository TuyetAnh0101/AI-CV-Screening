using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Privacy;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Jobs;
using SmartCv.Tests.Runs;

namespace SmartCv.Tests;

/// <summary>
/// A23: GET /api/cvs end-to-end through the real API pipeline (gate, middleware, EF) on an in-memory database.
/// Data is synthetic. SQL Server collation, LIKE escaping and speed are covered by A23CvListSqlServerTests (opt-in).
/// </summary>
public class A23CvListEndpointTests
{
    internal static readonly DateTime T0 = new(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);
    internal sealed record Seed(Dictionary<string, Guid> Cv, int Job1, int Job2);

    /// <summary>A..H in upload order (A newest). E = anonymized by A39, F = legacy anonymized row that still holds personal data.</summary>
    internal static async Task<Seed> SeedAsync(SmartCvDbContext db)
    {
        var jobs = await db.Jobs.OrderBy(j => j.JobId).Select(j => j.JobId).Take(2).ToListAsync();
        int job1 = jobs[0], job2 = jobs[1];
        var prompt = new PromptVersion { VersionName = "A23 prompt", SystemPrompt = "synthetic", IsActive = true };
        EvaluationRun Run(string type, int minutes, string status = RunStatuses.Completed) => new()
        {
            RunType = type, Status = status, PromptVersion = prompt, ModelName = "fixture", Temperature = .2m, KeywordWeight = .3m,
            AiWeight = .7m, PassThreshold = 70, CreatedAt = T0.AddMinutes(minutes), CompletedAt = T0.AddMinutes(minutes + 1)
        };
        Cv Make(string file, int hoursAgo, string status, string? source, string? text, Candidate? candidate, DateTime? anonymizedAt = null)
        {
            var cv = JobCrudEndpointTests.Cv();
            cv.OriginalFileName = file; cv.StoredPath = "PRIVATE-PATH/" + cv.CvId; cv.UploadedAt = T0.AddHours(-hoursAgo);
            cv.ExtractionStatus = status; cv.SourceChannel = source; cv.ExtractedText = text; cv.Candidate = candidate; cv.AnonymizedAt = anonymizedAt;
            return cv;
        }
        Application Apply(Cv cv, int job, string stage, string? reason = null, int minutes = 0) => new()
        { Cv = cv, JobId = job, Stage = stage, RejectReason = reason, CreatedAt = T0.AddMinutes(minutes), StageUpdatedAt = stage == ApplicationStages.New ? null : T0.AddMinutes(minutes + 5) };

        var cvs = new Dictionary<string, Cv>
        {
            ["A"] = Make("cv-nguyen-van-an.pdf", 1, ExtractionStatuses.Completed, CvSourceChannels.Email, "Kỹ năng: React, SQL Server, Docker",
                new Candidate { FullName = "Nguyễn Văn An", Email = "an.nguyen@example.com", PhoneNumber = "0912000111", CreatedAt = T0 }),
            ["B"] = Make("tran-thi-binh.pdf", 2, ExtractionStatuses.Completed, CvSourceChannels.Website, "Java Spring Boot, PostgreSQL",
                new Candidate { FullName = "Trần Thị Bình", Email = "binh.tran@example.com", CreatedAt = T0 }),
            ["C"] = Make("scan-pending.png", 3, ExtractionStatuses.Pending, CvSourceChannels.JobBoard, null, null),
            ["D"] = Make("cv-loi.pdf", 4, ExtractionStatuses.Failed, CvSourceChannels.Referral, null, new Candidate { FullName = "Lê Hoàng", CreatedAt = T0 }),
            ["E"] = Make(RetentionRules.AnonymizedFileName, 5, ExtractionStatuses.Completed, CvSourceChannels.Email, null, null, T0.AddHours(-1)),
            ["F"] = Make("PRIVATE-legacy.pdf", 6, ExtractionStatuses.Completed, CvSourceChannels.Other, "React SQL Server PRIVATE TEXT",
                new Candidate { FullName = "PRIVATE Legacy Name", Email = "private.legacy@example.com", CreatedAt = T0 }, T0.AddHours(-2)),
            ["G"] = Make("React-Developer-CV.pdf", 7, ExtractionStatuses.Completed, null, "khác", null),
            ["H"] = Make("only-prompt-test.pdf", 8, ExtractionStatuses.Completed, CvSourceChannels.Website, "Kotlin", null)
        };
        cvs["B"].RetainUntil = T0.AddDays(30); cvs["E"].RetainUntil = T0.AddDays(-1);
        var a1 = Apply(cvs["A"], job1, ApplicationStages.Reviewing, minutes: -60);
        var a2 = Apply(cvs["A"], job2, ApplicationStages.Rejected, RejectReasons.MissingSkills, -50);
        var h1 = Apply(cvs["H"], job1, ApplicationStages.New, minutes: -40);
        db.AddRange(a1, a2, h1, Apply(cvs["B"], job1, ApplicationStages.Rejected, RejectReasons.NotEnoughExperience, -30),
            Apply(cvs["D"], job2, ApplicationStages.New, minutes: -20), Apply(cvs["E"], job1, ApplicationStages.Rejected, RejectReasons.OtherRequirement, -10));
        db.AddRange(cvs["C"], cvs["F"], cvs["G"]);

        var old = Run(RunTypes.Screening, 10); var latest = Run(RunTypes.Screening, 20); var test = Run(RunTypes.PromptTest, 30);
        var failed = Run(RunTypes.Screening, 40, RunStatuses.Failed); var olderOtherJob = Run(RunTypes.Screening, 5);
        Evaluation Score(Application app, string status, decimal? final, string? fit, int minutes) =>
            new() { Application = app, Status = status, FinalScore = final, KeywordScore = final, FitLabel = fit, CreatedAt = T0.AddMinutes(minutes) };
        old.Evaluations.Add(Score(a1, EvaluationStatuses.Completed, 70, "Phù hợp", 10));
        latest.Evaluations.Add(Score(a1, EvaluationStatuses.Completed, 82, "Phù hợp", 20));
        test.Evaluations.Add(Score(a1, EvaluationStatuses.Completed, 95, "Rất phù hợp", 30));     // prompt test: never counted
        failed.Evaluations.Add(Score(a1, EvaluationStatuses.Failed, null, null, 40));            // failed: never counted
        olderOtherJob.Evaluations.Add(Score(a2, EvaluationStatuses.Completed, 60, "Tiềm năng", 5));
        test.Evaluations.Add(Score(h1, EvaluationStatuses.Completed, 90, "Rất phù hợp", 31));
        db.AddRange(old, latest, test, failed, olderOtherJob);
        await db.SaveChangesAsync();
        return new(cvs.ToDictionary(p => p.Key, p => p.Value.CvId), job1, job2);
    }

    private static async Task<(WebApplicationFactory<Program> Factory, HttpClient Client, Seed Seed)> StartAsync(bool seed = true)
    {
        var factory = RunHistoryEndpointTests.Factory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            await db.Database.EnsureCreatedAsync();
            var data = seed ? await SeedAsync(db) : new Seed(new Dictionary<string, Guid>(), 1, 2);
            return (factory, factory.CreateClient(), data);
        }
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("/api/cvs" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Names(JsonElement page, Seed seed) => page.GetProperty("items").EnumerateArray()
        .Select(i => seed.Cv.Single(p => p.Value == i.GetProperty("cvId").GetGuid()).Key).ToArray();

    private static JsonElement Item(JsonElement page, Seed seed, string name) =>
        page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("cvId").GetGuid() == seed.Cv[name]);

    [Fact]
    public async Task List_is_newest_first_with_the_documented_contract_and_no_internal_fields()
    {
        var (factory, client, seed) = await StartAsync(); using var _ = factory; using var __ = client;
        using var response = await client.GetAsync("/api/cvs");
        var raw = await response.Content.ReadAsStringAsync(); var page = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(new[] { "items", "page", "pageSize", "totalCount", "totalPages", "elapsedMs" }, page.EnumerateObject().Select(p => p.Name));
        Assert.Equal((1, 20, 8, 1), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("totalCount").GetInt32(), page.GetProperty("totalPages").GetInt32()));
        Assert.True(page.GetProperty("elapsedMs").GetInt64() >= 0);
        Assert.Equal(new[] { "A", "B", "C", "D", "E", "F", "G", "H" }, Names(page, seed));
        var a = Item(page, seed, "A");
        Assert.Equal(new[] { "cvId", "fileName", "uploadedAt", "extractionStatus", "sourceChannel", "sourceChannelName", "retainUntil", "anonymizedAt",
            "isAnonymized", "candidateId", "candidateName", "candidateEmail", "applications", "latestScore" }.Order(), a.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(new[] { "applicationId", "jobId", "jobTitle", "stage", "rejectReason", "stageUpdatedAt", "latestFinalScore", "latestFitLabel", "latestEvaluatedAt" }.Order(),
            a.GetProperty("applications")[0].EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(("cv-nguyen-van-an.pdf", "Nguyễn Văn An", "an.nguyen@example.com", "Email ứng tuyển"),
            (a.GetProperty("fileName").GetString(), a.GetProperty("candidateName").GetString(), a.GetProperty("candidateEmail").GetString(), a.GetProperty("sourceChannelName").GetString()));
        Assert.EndsWith("Z", a.GetProperty("uploadedAt").GetString()); Assert.False(a.GetProperty("isAnonymized").GetBoolean());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("retainUntil").ValueKind);
        Assert.EndsWith("Z", Item(page, seed, "B").GetProperty("retainUntil").GetString());
        var g = Item(page, seed, "G");
        Assert.Equal(JsonValueKind.Null, g.GetProperty("sourceChannel").ValueKind); Assert.Equal(CvSourceChannels.DisplayName(null), g.GetProperty("sourceChannelName").GetString());
        Assert.Equal(JsonValueKind.Null, g.GetProperty("candidateId").ValueKind);
        // Never internal paths, hashes or CV text (only searchable, not returned).
        foreach (var hidden in new[] { "PRIVATE-PATH", "storedPath", "fileHash", "extractedText", "Kỹ năng: React", "PRIVATE" }) Assert.DoesNotContain(hidden, raw);
    }

    [Fact]
    public async Task Each_job_shows_its_current_stage_and_only_the_latest_completed_screening_score()
    {
        var (factory, client, seed) = await StartAsync(); using var _ = factory; using var __ = client;
        var page = await GetAsync(client, "");
        var a = Item(page, seed, "A"); var apps = a.GetProperty("applications").EnumerateArray().ToArray();
        Assert.Equal(new[] { seed.Job1, seed.Job2 }, apps.Select(x => x.GetProperty("jobId").GetInt32()));      // ordered by application date
        Assert.Equal((ApplicationStages.Reviewing, 82m, "Phù hợp"), (apps[0].GetProperty("stage").GetString(), apps[0].GetProperty("latestFinalScore").GetDecimal(), apps[0].GetProperty("latestFitLabel").GetString()));
        Assert.Equal(T0.AddMinutes(20), apps[0].GetProperty("latestEvaluatedAt").GetDateTime().ToUniversalTime());   // not the newer prompt test / failed run
        Assert.Equal((ApplicationStages.Rejected, RejectReasons.MissingSkills, 60m), (apps[1].GetProperty("stage").GetString(), apps[1].GetProperty("rejectReason").GetString(), apps[1].GetProperty("latestFinalScore").GetDecimal()));
        Assert.EndsWith("Z", apps[1].GetProperty("stageUpdatedAt").GetString());
        Assert.False(string.IsNullOrEmpty(apps[0].GetProperty("jobTitle").GetString()));
        Assert.Equal(82m, a.GetProperty("latestScore").GetDecimal());                                       // most recent screening across jobs
        var h = Item(page, seed, "H");
        Assert.Equal(JsonValueKind.Null, h.GetProperty("latestScore").ValueKind);                            // only a prompt-test score
        Assert.Equal(JsonValueKind.Null, h.GetProperty("applications")[0].GetProperty("latestFinalScore").ValueKind);
        Assert.Equal(JsonValueKind.Null, Item(page, seed, "B").GetProperty("latestScore").ValueKind);
        Assert.Empty(Item(page, seed, "C").GetProperty("applications").EnumerateArray());
    }

    [Theory]
    [InlineData("React", "A,G")]                    // CV text + file name; legacy anonymized F has "React" but is excluded
    [InlineData("Trần Thị", "B")]                   // candidate name
    [InlineData("binh.tran@", "B")]                 // candidate email
    [InlineData("an.nguyen@example.com", "A")]
    [InlineData("  SQL    Server  ", "A")]          // whitespace collapsed
    [InlineData("cv-loi", "D")]
    [InlineData("ẩn danh", "")]                     // A39 placeholder file name never makes an anonymized CV findable
    [InlineData("PRIVATE", "")]                     // legacy anonymized row: name, email, file, text all excluded
    [InlineData("khongcotrongcv", "")]
    public async Task Search_covers_file_name_candidate_name_email_and_CV_text_but_never_anonymized_CVs(string search, string expected)
    {
        var (factory, client, seed) = await StartAsync(); using var _ = factory; using var __ = client;
        var page = await GetAsync(client, "?search=" + Uri.EscapeDataString(search));
        Assert.Equal(Expected(expected), Names(page, seed));
        Assert.Equal(Names(page, seed).Length, page.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("jobId={1}", "A,B,E,H")]
    [InlineData("jobId={1}&stage=Rejected", "B,E")]         // stage at THAT job: A is Rejected only at job 2
    [InlineData("jobId={2}&stage=Rejected", "A")]
    [InlineData("stage=Rejected", "A,B,E")]
    [InlineData("stage=New", "D,H")]
    [InlineData("jobId=999999", "")]
    [InlineData("source=Website", "B,H")]
    [InlineData("status=Pending", "C")]
    [InlineData("status=Failed", "D")]
    [InlineData("status=Completed", "A,B,G,H")]              // extraction status excludes anonymized CVs
    [InlineData("status=Anonymized", "E,F")]
    [InlineData("search=React&source=Email", "A")]
    [InlineData("search=React&status=Anonymized", "")]
    [InlineData("jobId={1}&stage=Rejected&status=Anonymized", "E")]
    public async Task Filters_combine_and_stage_applies_to_the_selected_job(string query, string expected)
    {
        var (factory, client, seed) = await StartAsync(); using var _ = factory; using var __ = client;
        var page = await GetAsync(client, "?" + query.Replace("{1}", seed.Job1.ToString()).Replace("{2}", seed.Job2.ToString()));
        Assert.Equal(Expected(expected), Names(page, seed));
    }

    private static string[] Expected(string csv) => csv.Length == 0 ? Array.Empty<string>() : csv.Split(',');

    [Fact]
    public async Task Anonymized_rows_are_listed_without_any_personal_field()
    {
        var (factory, client, seed) = await StartAsync(); using var _ = factory; using var __ = client;
        using var response = await client.GetAsync("/api/cvs?status=Anonymized");
        var raw = await response.Content.ReadAsStringAsync(); var page = JsonDocument.Parse(raw).RootElement;
        foreach (var name in new[] { "E", "F" })
        {
            var row = Item(page, seed, name);
            Assert.Equal(RetentionRules.AnonymizedFileName, row.GetProperty("fileName").GetString());
            Assert.True(row.GetProperty("isAnonymized").GetBoolean()); Assert.EndsWith("Z", row.GetProperty("anonymizedAt").GetString());
            foreach (var field in new[] { "candidateId", "candidateName", "candidateEmail" }) Assert.Equal(JsonValueKind.Null, row.GetProperty(field).ValueKind);
        }
        Assert.Equal(ApplicationStages.Rejected, Item(page, seed, "E").GetProperty("applications")[0].GetProperty("stage").GetString()); // HR history kept
        Assert.DoesNotContain("PRIVATE", raw); Assert.DoesNotContain("private.legacy", raw);
    }

    [Fact]
    public async Task Paging_is_stable_without_overlap_and_a_page_beyond_the_end_is_empty()
    {
        var (factory, client, seed) = await StartAsync(); using var _ = factory; using var __ = client;
        var pages = new List<string>();
        for (var p = 1; p <= 3; p++)
        {
            var page = await GetAsync(client, $"?page={p}&pageSize=3");
            Assert.Equal((p, 3, 8, 3), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("totalCount").GetInt32(), page.GetProperty("totalPages").GetInt32()));
            pages.AddRange(Names(page, seed));
        }
        Assert.Equal(new[] { "A", "B", "C", "D", "E", "F", "G", "H" }, pages);
        var beyond = await GetAsync(client, "?page=4&pageSize=3");
        Assert.Empty(beyond.GetProperty("items").EnumerateArray()); Assert.Equal(8, beyond.GetProperty("totalCount").GetInt32());
        var huge = await GetAsync(client, "?page=107374183&pageSize=20");
        Assert.Empty(huge.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Empty_library_returns_zero_pages()
    {
        var (factory, client, _) = await StartAsync(seed: false); using var f = factory; using var c = client;
        var page = await GetAsync(client, "?search=React");
        Assert.Equal((1, 20, 0, 0), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("totalCount").GetInt32(), page.GetProperty("totalPages").GetInt32()));
        Assert.Empty(page.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("search=a", "search")]
    [InlineData("search=%20%20a%20%20", "search")]
    [InlineData("stage=Hired", "stage")]
    [InlineData("stage=new", "stage")]
    [InlineData("source=Facebook", "source")]
    [InlineData("status=Done", "status")]
    [InlineData("status=anonymized", "status")]
    [InlineData("page=0", "page")]
    [InlineData("page=abc", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("jobId=0", "jobId")]
    [InlineData("jobId=abc", "jobId")]
    [InlineData("page=1&page=2", "page")]
    [InlineData("search=ab&SEARCH=cd", "search")]
    [InlineData("page=107374184", "page")]
    public async Task Invalid_parameters_return_Vietnamese_ProblemDetails(string query, string field)
    {
        var (factory, client, _) = await StartAsync(seed: false); using var f = factory; using var c = client;
        using var response = await client.GetAsync("/api/cvs?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Bộ lọc không hợp lệ", json.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("detail").GetString()));
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out var messages));
        Assert.False(string.IsNullOrWhiteSpace(messages[0].GetString()));
    }

    [Fact]
    public async Task Search_too_long_is_rejected()
    {
        var (factory, client, _) = await StartAsync(seed: false); using var f = factory; using var c = client;
        using var response = await client.GetAsync("/api/cvs?search=" + new string('x', 101));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("tối đa 100", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Listing_is_read_only()
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        async Task<string> Snapshot()
        {
            using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var cvs = await db.Cvs.AsNoTracking().OrderBy(x => x.CvId).Select(x => new { x.CvId, x.CandidateId, x.ExtractionStatus, x.AnonymizedAt, x.OriginalFileName, x.RetainUntil }).ToListAsync();
            var apps = await db.Applications.AsNoTracking().OrderBy(x => x.ApplicationId).Select(x => new { x.ApplicationId, x.Stage, x.StageVersion, x.StageUpdatedAt }).ToListAsync();
            return JsonSerializer.Serialize(new { cvs, apps, Candidates = await db.Candidates.CountAsync(), Evaluations = await db.Evaluations.CountAsync(), Events = await db.ApplicationEvents.CountAsync() });
        }
        var before = await Snapshot();
        foreach (var query in new[] { "", "?search=React", "?status=Anonymized", "?jobId=1&stage=Rejected", "?page=0" }) (await client.GetAsync("/api/cvs" + query)).Dispose();
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task List_is_not_mapped_when_the_CV_API_gate_is_off()
    {
        using var factory = RunHistoryEndpointTests.Factory(enabled: false); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/cvs");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
