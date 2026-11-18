using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Jobs;

namespace SmartCv.Tests.Candidates;

public class A43EndpointTests
{
    internal static readonly DateTime Now = new(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);
    internal sealed record Seed(int CandidateId, Guid[] Cvs, int[] Applications);
    internal static async Task<Seed> SeedAsync(SmartCvDbContext db, ICvFileStorage? storage = null)
    {
        var candidate = new Candidate { FullName = "Nguyễn Thử Xóa", Email = "thu.xoa@example.com", PhoneNumber = "0912000111", CreatedAt = Now };
        var cvs = new[] { JobCrudEndpointTests.Cv(), JobCrudEndpointTests.Cv() };
        foreach (var cv in cvs)
        {
            cv.Candidate = candidate; cv.UploadedAt = Now; cv.OriginalFileName = "cv-thu-xoa.pdf";
            cv.ExtractionStatus = ExtractionStatuses.Completed; cv.SourceChannel = "Email";
            cv.ConsentConfirmedAt = Now; cv.RetainUntil = Now.AddMonths(6);
            cv.ExtractedText = "Nguyễn Thử Xóa\nthu.xoa@example.com\n0912000111\n.NET";
            cv.ExtractionError = "PRIVATE_EXTRACTION";
            if (storage is not null)
            {
                using var file = new MemoryStream(TestSupport.Pdf("A43 synthetic " + cv.CvId));
                cv.StoredPath = await storage.SaveAsync(cv.CvId, ".pdf", file, Now);
            }
            else cv.StoredPath = "Uploads/CV/PRIVATE_PATH.pdf";
        }
        var apps = new[]
        {
            new Application { Cv = cvs[0], JobId = 1, Stage = ApplicationStages.Interview, CreatedAt = Now },
            new Application { Cv = cvs[1], JobId = 2, Stage = ApplicationStages.Rejected, RejectReason = RejectReasons.MissingSkills, CreatedAt = Now }
        };
        db.AddRange(apps);
        db.ApplicationEvents.Add(new ApplicationEvent { Application = apps[0], FromStage = "New", ToStage = "Interview", Actor = "hr.before", Note = "Initial decision", CreatedAt = Now });
        var prompt = new PromptVersion { VersionName = "A43 synthetic", SystemPrompt = "PRIVATE_SYSTEM_PROMPT" };
        var run = new EvaluationRun { PromptVersion = prompt, RunType = RunTypes.Screening, Status = RunStatuses.Completed, ModelName = "fake",
            KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70, CreatedAt = Now };
        var score = new Evaluation { Application = apps[0], Run = run, KeywordScore = 80, AiScore = 90, FinalScore = 87, FitLabel = "Phù hợp",
            Status = EvaluationStatuses.Completed, Reasoning = "Nhận xét tổng hợp", AiRawResponse = "PRIVATE_RAW_AI", ErrorMessage = "PRIVATE_ERROR", CreatedAt = Now };
        var skill = await db.JobSkills.Where(s => s.JobId == 1).FirstAsync();
        score.Skills.Add(new EvaluationSkill { JobId = 1, SkillCode = skill.SkillCode, IsMatched = true, Evidence = "Đoạn bằng chứng" });
        db.Evaluations.Add(score);
        db.HrLabels.Add(new HrLabel { Application = apps[0], EvaluationSet = new EvaluationSet { Name = "A43 synthetic set" }, IsPass = true, Note = "Nhãn thử", LabeledBy = "hr.label", LabeledAt = Now });
        await db.SaveChangesAsync();
        return new(candidate.CandidateId, cvs.Select(v => v.CvId).ToArray(), apps.Select(a => a.ApplicationId).ToArray());
    }

    private static async Task<(WebApplicationFactory<Program> Factory, HttpClient Client, Seed Seed)> StartAsync(bool auth = false, SaveChangesInterceptor? interceptor = null)
    {
        var factory = JobCrudEndpointTests.Factory();
        if (auth) factory = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddAuthentication("A43Test").AddScheme<AuthenticationSchemeOptions, Auth>("A43Test", _ => { })));
        if (interceptor is not null) factory = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddDbContext<SmartCvDbContext>(o => o.AddInterceptors(interceptor))));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        var seed = await SeedAsync(db);
        return (factory, factory.CreateClient(), seed);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Export_contains_all_documented_data_UTF8_UTC_and_audits_once_before_returning_file()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.GetAsync($"/api/candidates/{seed.CandidateId}/data-export?handledBy=hr.test");
        var data = await JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("du-lieu-ung-vien-", response.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("Nguyễn Thử Xóa", raw);
        Assert.DoesNotContain("PRIVATE", raw);
        foreach (var internalField in new[] { "storedPath", "fileHash", "aiRawResponse", "systemPrompt" }) Assert.DoesNotContain(internalField, raw);
        Assert.EndsWith("Z", data.GetProperty("exportedAt").GetString());
        Assert.Equal(2, data.GetProperty("cvs").GetArrayLength());
        var cv = data.GetProperty("cvs").EnumerateArray().Single(v => v.GetProperty("applications")[0].GetProperty("evaluations").GetArrayLength() > 0);
        Assert.Contains("thu.xoa@example.com", cv.GetProperty("extractedText").GetString());
        var app = cv.GetProperty("applications")[0];
        Assert.Equal(87, app.GetProperty("evaluations")[0].GetProperty("finalScore").GetDecimal());
        Assert.Equal("Đoạn bằng chứng", app.GetProperty("evaluations")[0].GetProperty("skills")[0].GetProperty("evidence").GetString());
        Assert.Equal("hr.before", app.GetProperty("events")[0].GetProperty("actor").GetString());
        Assert.True(app.GetProperty("hrLabels")[0].GetProperty("isPass").GetBoolean());
        Assert.Empty(data.GetProperty("previousRequests").EnumerateArray());
        using var second = await client.GetAsync($"/api/candidates/{seed.CandidateId}/data-export");
        Assert.Single((await JsonAsync(second, HttpStatusCode.OK)).GetProperty("previousRequests").EnumerateArray());
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var logs = await db.CandidateDataRequests.OrderBy(r => r.RequestId).ToListAsync();
        Assert.Equal(2, logs.Count); Assert.Equal("hr.test", logs[0].HandledBy); Assert.Equal("HR", logs[1].HandledBy);
        Assert.Equal(2, logs[0].CvIds!.Split(',').Length);
        Assert.DoesNotContain("thu.xoa", JsonSerializer.Serialize(logs));
        Assert.Equal(1, await db.Candidates.CountAsync()); Assert.Equal(2, await db.Cvs.CountAsync());
        Assert.Equal(87, (await db.Evaluations.SingleAsync()).FinalScore);
        using var detail = await client.GetAsync($"/api/candidates/{seed.CandidateId}");
        Assert.Equal(2, (await JsonAsync(detail, HttpStatusCode.OK)).GetProperty("dataRequests").GetArrayLength());
    }

    [Theory]
    [InlineData("handledBy=one&handledBy=two")]
    [InlineData("handledBy=%0A")]
    public async Task Invalid_export_query_returns_400_without_journal(string query)
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.GetAsync($"/api/candidates/{seed.CandidateId}/data-export?{query}");
        await JsonAsync(response, HttpStatusCode.BadRequest);
        using var scope = f.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().CandidateDataRequests.ToListAsync());
    }

    [Theory]
    [InlineData(false, "hr.test", null, "confirm")]
    [InlineData(true, null, null, "handledBy")]
    [InlineData(true, " ", null, "handledBy")]
    public async Task Erase_requires_explicit_confirmation_and_operator_without_changes(bool confirm, string? actor, string? note, string field)
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", new { confirm, handledBy = actor, note });
        Assert.True((await JsonAsync(response, HttpStatusCode.BadRequest)).GetProperty("errors").TryGetProperty(field, out _));
        using var scope = f.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.Equal("Interview", (await db.Applications.FindAsync(seed.Applications[0]))!.Stage);
        Assert.Empty(await db.CandidateDataRequests.ToListAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"confirm\":\"true\",\"handledBy\":\"hr.test\"}")]
    [InlineData("{\"confirm\":true,\"Confirm\":false,\"handledBy\":\"hr.test\"}")]
    public async Task Erase_malformed_body_is_a_safe_400(string body)
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.PostAsync($"/api/candidates/{seed.CandidateId}/erase", new StringContent(body, Encoding.UTF8, "application/json"));
        await JsonAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Erase_refuses_unsupported_provider_instead_of_pretending_to_commit()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", new { confirm = true, handledBy = "hr.test" });
        await JsonAsync(response, HttpStatusCode.ServiceUnavailable);
        using var scope = f.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.Equal(1, await db.Candidates.CountAsync()); Assert.Equal(2, await db.Cvs.CountAsync(v => v.AnonymizedAt == null));
        Assert.Equal("Interview", (await db.Applications.FindAsync(seed.Applications[0]))!.Stage);
        Assert.Empty(await db.CandidateDataRequests.ToListAsync());
    }

    [Fact]
    public async Task Missing_candidate_is_404_and_recorded_erasure_returns_410_with_metadata_without_new_audit()
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        var body = new { confirm = true, handledBy = "hr.test" };
        using var missing = await client.PostAsJsonAsync("/api/candidates/999999/erase", body);
        await JsonAsync(missing, HttpStatusCode.NotFound);
        using var export = await client.GetAsync("/api/candidates/999999/data-export"); await JsonAsync(export, HttpStatusCode.NotFound);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            db.CandidateDataRequests.Add(new CandidateDataRequest { CandidateId = 999999, RequestType = "Erase", HandledBy = "hr.test", CreatedAt = Now });
            await db.SaveChangesAsync();
        }
        using var repeated = await client.PostAsJsonAsync("/api/candidates/999999/erase", body);
        var error = await JsonAsync(repeated, HttpStatusCode.Gone);
        Assert.True(error.GetProperty("requestId").GetInt64() > 0); Assert.EndsWith("Z", error.GetProperty("handledAt").GetString());
        using var journal = await client.GetAsync("/api/data-requests?candidateId=999999&type=Erase");
        Assert.Single((await JsonAsync(journal, HttpStatusCode.OK)).EnumerateArray());
    }

    [Theory]
    [InlineData("candidateId=abc", "candidateId")]
    [InlineData("candidateId=0", "candidateId")]
    [InlineData("candidateId=-1", "candidateId")]
    [InlineData("candidateId=2147483648", "candidateId")]
    [InlineData("candidateId=1&candidateId=2", "candidateId")]
    [InlineData("type=Other", "type")]
    [InlineData("type=erase", "type")]
    [InlineData("type=Export&type=Erase", "type")]
    public async Task Journal_query_validation_is_explicit(string query, string field)
    {
        var (factory, client, _) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.GetAsync("/api/data-requests?" + query);
        Assert.True((await JsonAsync(response, HttpStatusCode.BadRequest)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Journal_filters_orders_and_caps_at_200_without_internal_CvIds()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            db.CandidateDataRequests.AddRange(Enumerable.Range(1, 205).Select(i => new CandidateDataRequest
            { CandidateId = seed.CandidateId, RequestType = i % 2 == 0 ? "Export" : "Erase", HandledBy = "hr.test", CvIds = "PRIVATE_IDS", CreatedAt = Now }));
            await db.SaveChangesAsync();
        }
        using var all = await client.GetAsync("/api/data-requests");
        var rows = (await JsonAsync(all, HttpStatusCode.OK)).EnumerateArray().ToArray();
        Assert.Equal(200, rows.Length);
        Assert.True(rows[0].GetProperty("requestId").GetInt64() > rows[^1].GetProperty("requestId").GetInt64());
        Assert.DoesNotContain("PRIVATE", await all.Content.ReadAsStringAsync());
        using var filtered = await client.GetAsync($"/api/data-requests?candidateId={seed.CandidateId}&type=Erase");
        Assert.Equal(103, (await JsonAsync(filtered, HttpStatusCode.OK)).GetArrayLength());
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("Viewer", 403)]
    [InlineData("HR", 200)]
    [InlineData("Admin", 200)]
    public async Task Configured_authentication_protects_export_journal_and_erase(string? role, int expected)
    {
        var (factory, client, seed) = await StartAsync(auth: true); using var f = factory; using var c = client;
        if (role is not null) client.DefaultRequestHeaders.Add("X-A43-Role", role);
        using var journal = await client.GetAsync("/api/data-requests"); Assert.Equal(expected, (int)journal.StatusCode);
        using var export = await client.GetAsync($"/api/candidates/{seed.CandidateId}/data-export"); Assert.Equal(expected, (int)export.StatusCode);
        using var erase = await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase", new { confirm = true, handledBy = "hr.test" });
        Assert.Equal(expected == 200 ? 503 : expected, (int)erase.StatusCode);
        using var detail = await client.GetAsync($"/api/candidates/{seed.CandidateId}");
        var data = await JsonAsync(detail, HttpStatusCode.OK);
        Assert.Equal(expected == 200 ? 1 : 0, data.GetProperty("dataRequests").GetArrayLength());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Disabled_gate_leaves_all_A43_routes_absent(string environment)
    {
        using var factory = JobCrudEndpointTests.Factory(environment, false); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/data-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/candidates/1/data-export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/candidates/1/erase", new { confirm = true, handledBy = "hr.test" })).StatusCode);
    }

    private sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-A43-Role"].FirstOrDefault();
            if (role is null) return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "synthetic"), new Claim(ClaimTypes.Role, role)], Scheme.Name)), Scheme.Name)));
        }
    }

    [Theory]
    [InlineData(true, 503)]
    [InlineData(false, 500)]
    public async Task Export_save_errors_are_safe_and_never_retried(bool timeout, int status)
    {
        var fault = new SaveFault(timeout);
        var (factory, client, seed) = await StartAsync(interceptor: fault); using var f = factory; using var c = client;
        fault.Enabled = true;
        using var response = await client.GetAsync($"/api/candidates/{seed.CandidateId}/data-export");
        await JsonAsync(response, (HttpStatusCode)status);
        Assert.DoesNotContain("PRIVATE", await response.Content.ReadAsStringAsync()); Assert.Equal(1, fault.Attempts);
        using var scope = f.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.Empty(await db.CandidateDataRequests.ToListAsync()); Assert.Equal(1, await db.Candidates.CountAsync());
    }

    [Fact]
    public async Task Export_hides_legacy_personal_payload_on_CVs_already_marked_anonymized()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var cv = await db.Cvs.SingleAsync(v => v.CvId == seed.Cvs[0]);
            cv.AnonymizedAt = Now; cv.ExtractedText = "PRIVATE_LEGACY_TEXT"; cv.OriginalFileName = "PRIVATE_LEGACY_NAME.pdf";
            var score = await db.Evaluations.Include(e => e.Skills).SingleAsync();
            score.Reasoning = "PRIVATE_LEGACY_REASON"; score.Skills.Single().Evidence = "PRIVATE_LEGACY_EVIDENCE";
            await db.SaveChangesAsync();
        }
        using var response = await client.GetAsync($"/api/candidates/{seed.CandidateId}/data-export");
        await JsonAsync(response, HttpStatusCode.OK);
        Assert.DoesNotContain("PRIVATE", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(101, 0, 400)]
    [InlineData(5, 501, 400)]
    [InlineData(100, 500, 503)]
    public async Task Erase_checks_operator_and_note_length_before_any_write(int actorLength, int noteLength, int status)
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.PostAsJsonAsync($"/api/candidates/{seed.CandidateId}/erase",
            new { confirm = true, handledBy = new string('a', actorLength), note = new string('b', noteLength) });
        await JsonAsync(response, (HttpStatusCode)status);
    }

    [Fact]
    public async Task Export_rejects_an_overlong_operator_instead_of_silently_truncating()
    {
        var (factory, client, seed) = await StartAsync(); using var f = factory; using var c = client;
        using var response = await client.GetAsync($"/api/candidates/{seed.CandidateId}/data-export?handledBy=" + new string('a', 101));
        await JsonAsync(response, HttpStatusCode.BadRequest);
    }

    private sealed class SaveFault(bool timeout) : SaveChangesInterceptor
    {
        internal bool Enabled;
        internal int Attempts;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!Enabled) return ValueTask.FromResult(result);
            Attempts++;
            return ValueTask.FromException<InterceptionResult<int>>(timeout
                ? new TimeoutException("PRIVATE synthetic") : new InvalidOperationException("PRIVATE synthetic"));
        }
    }

}
