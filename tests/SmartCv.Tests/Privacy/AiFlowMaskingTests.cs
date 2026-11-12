using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

/// <summary>
/// A37 trên đủ ba luồng gọi AI, qua API thật (TestServer, database InMemory) và GeminiEvaluationService thật với HttpMessageHandler giả
/// (khóa giả, không gọi mạng): AI Preview, lượt chấm chính thức (POST /api/runs → tiến trình chấm) và Prompt Test
/// (bộ CV có nhãn HR → POST /api/prompt-tests → tiến trình chấm). Mọi payload gửi Gemini đã che; System Prompt gửi nguyên văn;
/// database giữ nội dung gốc. Dữ liệu ứng viên là dữ liệu giả.
/// </summary>
public class AiFlowMaskingTests
{
    private const int BackendJob = 1;
    private const string ScreeningPrompt = "PROMPT-SCREENING-A37";
    private const string TestPrompt = "PROMPT-TEST-A37";

    // Biết tên trong bảng Candidates, CV viết tên khác thứ tự
    private const string KnownName = "Trần Minh Khoa";
    private const string KnownCv =
        "Backend Developer\n" +
        "Tôi là Khoa Trần Minh. Email: khoa.tran@example.com | SĐT: 0900 123 456\n" +
        "GPA 3.2. 1.5 năm làm C#, ASP.NET Core, SQL Server, EF Core, Git, Docker. CI/CD bằng GitHub Actions.";

    // Chưa gắn ứng viên, tên nằm dưới chức danh
    private const string UnknownCv =
        "Lập trình viên Backend .NET\n" +
        "Lê Thu Hà\n" +
        "Email: ha.le@example.com | linkedin.com/in/le-thu-ha\n" +
        "GPA 3.4. 2 năm C#, ASP.NET Core, SQL Server, Docker, Git.";

    private static readonly string[] RawPersonalData =
        ["Khoa Trần Minh", KnownName, "khoa.tran@example.com", "0900 123 456", "Lê Thu Hà", "ha.le@example.com", "le-thu-ha", "example.com"];

    private sealed record Env(WebApplicationFactory<Program> Factory, HttpClient Client, GeminiPayloadCapture Handler) : IDisposable
    {
        public int Known { get; set; }
        public int Unknown { get; set; }
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }

        public async Task<T> DbAsync<T>(Func<SmartCvDbContext, Task<T>> action)
        {
            using var scope = Factory.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
        }

        /// <summary>Chạy lượt như tiến trình nền: RunScoringProcessor lấy từ DI, IAiEvaluator = GeminiEvaluationService với HTTP giả.</summary>
        public async Task ProcessAsync(int runId)
        {
            using var scope = Factory.Services.CreateScope();
            Assert.IsType<GeminiEvaluationService>(scope.ServiceProvider.GetRequiredService<SmartCv.Core.Ai.IAiEvaluator>());
            await scope.ServiceProvider.GetRequiredService<RunScoringProcessor>().ProcessRunAsync(runId, default);
        }
    }

    private static async Task<Env> CreateAsync()
    {
        var handler = new GeminiPayloadCapture();
        var factory = TestSupport.ApiFactory(settings: new()
            {
                ["Scoring:WorkerEnabled"] = "false",          // không chạy tiến trình nền; test tự gọi tiến trình chấm
                ["Gemini:MinDelayBetweenCallsMs"] = "0"
            })
            .WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddHttpClient<GeminiEvaluationService>().ConfigurePrimaryHttpMessageHandler(() => handler)));
        var env = new Env(factory, factory.CreateClient(), handler);
        await env.DbAsync(async db =>
        {
            await db.Database.EnsureCreatedAsync();
            db.PromptVersions.Add(new PromptVersion { VersionName = "v-a37-active", SystemPrompt = ScreeningPrompt, IsActive = true, CreatedAt = DateTime.UtcNow });
            db.PromptVersions.Add(new PromptVersion { VersionName = "v-a37-test", SystemPrompt = TestPrompt, IsActive = false, CreatedAt = DateTime.UtcNow });
            env.Known = await AddApplicationAsync(db, KnownCv, new Candidate { FullName = KnownName, CreatedAt = DateTime.UtcNow });
            env.Unknown = await AddApplicationAsync(db, UnknownCv, candidate: null);
            return 0;
        });
        return env;
    }

    private static async Task<int> AddApplicationAsync(SmartCvDbContext db, string text, Candidate? candidate)
    {
        var id = Guid.NewGuid();
        db.Cvs.Add(new Cv
        {
            CvId = id, Candidate = candidate, OriginalFileName = "CV.pdf", StoredPath = $"Uploads/CV/2026/11/{id}.pdf", ContentType = "application/pdf",
            FileSizeBytes = 1, FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = ExtractionStatuses.Completed,
            ExtractedText = text, UploadedAt = DateTime.UtcNow
        });
        var application = new Application { CvId = id, JobId = BackendJob, CreatedAt = DateTime.UtcNow };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return application.ApplicationId;
    }

    private static void AssertMasked(string body, string expectedSystemPrompt)
    {
        var user = GeminiPayloadCapture.UserText(body);
        foreach (var raw in RawPersonalData) Assert.DoesNotContain(raw, user, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[CANDIDATE]", user);
        Assert.Contains("[EMAIL]", user);
        Assert.Contains("ASP.NET Core", user);                       // nội dung chuyên môn còn nguyên
        Assert.Contains(SmartCv.Core.Ai.AiPromptBuilder.MaskedDataRule, user);
        Assert.Equal(expectedSystemPrompt, GeminiPayloadCapture.SystemText(body));   // System Prompt của phiên bản, không bị sửa
    }

    private static async Task AssertDatabaseKeepsOriginalAsync(Env env)
    {
        var texts = await env.DbAsync(db => db.Applications.Where(a => a.ApplicationId == env.Known || a.ApplicationId == env.Unknown)
            .OrderBy(a => a.ApplicationId).Select(a => a.Cv.ExtractedText).ToListAsync());
        Assert.Equal([KnownCv, UnknownCv], texts);
    }

    [Fact]
    public async Task Preview_masks_both_known_and_unknown_candidates()
    {
        using var env = await CreateAsync();

        foreach (var id in new[] { env.Known, env.Unknown })
        {
            using var response = await env.Client.PostAsync($"/api/applications/{id}/ai-preview", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(2, env.Handler.Bodies.Count);
        foreach (var body in env.Handler.Bodies) AssertMasked(body, ScreeningPrompt);
        Assert.Contains("Lập trình viên Backend .NET\n[CANDIDATE]\nEmail: [EMAIL] | [LINK]", GeminiPayloadCapture.UserText(env.Handler.Bodies[1]));
        await AssertDatabaseKeepsOriginalAsync(env);
    }

    [Fact]
    public async Task Screening_run_created_by_api_sends_only_masked_payloads()
    {
        using var env = await CreateAsync();

        using var created = await env.Client.PostAsJsonAsync("/api/runs", new StartRunRequest { ApplicationIds = [env.Known, env.Unknown] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var run = (await created.Content.ReadFromJsonAsync<RunProgressDto>())!;
        Assert.Equal(RunTypes.Screening, run.RunType);
        await env.ProcessAsync(run.RunId);

        Assert.Equal(2, env.Handler.Bodies.Count);
        foreach (var body in env.Handler.Bodies) AssertMasked(body, ScreeningPrompt);
        var statuses = await env.DbAsync(db => db.Evaluations.Where(e => e.RunId == run.RunId).Select(e => e.Status).ToListAsync());
        Assert.All(statuses, s => Assert.Equal(EvaluationStatuses.Completed, s));
        await AssertDatabaseKeepsOriginalAsync(env);
    }

    [Fact]
    public async Task Prompt_test_run_created_by_api_sends_only_masked_payloads()
    {
        using var env = await CreateAsync();

        using var setResponse = await env.Client.PostAsJsonAsync("/api/evaluation-sets", new CreateEvaluationSetRequest { Name = "Bộ CV A37 (giả)" });
        setResponse.EnsureSuccessStatusCode();
        var set = (await setResponse.Content.ReadFromJsonAsync<EvaluationSetDto>())!;
        using var labels = await env.Client.PutAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels", new SaveHrLabelsRequest
        {
            LabeledBy = "HR thử nghiệm",
            Labels = [new() { ApplicationId = env.Known, IsPass = true }, new() { ApplicationId = env.Unknown, IsPass = false }]
        });
        labels.EnsureSuccessStatusCode();
        var promptId = await env.DbAsync(db => db.PromptVersions.Where(p => p.SystemPrompt == TestPrompt).Select(p => p.PromptVersionId).SingleAsync());

        using var started = await env.Client.PostAsJsonAsync("/api/prompt-tests", new StartPromptTestRequest { PromptVersionId = promptId, EvaluationSetId = set.EvaluationSetId });
        started.EnsureSuccessStatusCode();
        var run = (await started.Content.ReadFromJsonAsync<PromptTestRunDto>())!;
        await env.ProcessAsync(run.RunId);

        Assert.Equal(2, env.Handler.Bodies.Count);
        foreach (var body in env.Handler.Bodies) AssertMasked(body, TestPrompt);   // System Prompt của phiên bản đang thử
        var saved = await env.DbAsync(db => db.EvaluationRuns.AsNoTracking().SingleAsync(r => r.RunId == run.RunId));
        Assert.Equal(RunTypes.PromptTest, saved.RunType);
        Assert.Equal(RunStatuses.Completed, saved.Status);
        Assert.NotNull(saved.AccuracyPercent);
        await AssertDatabaseKeepsOriginalAsync(env);
    }
}
