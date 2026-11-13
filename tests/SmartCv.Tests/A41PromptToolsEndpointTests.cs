using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Evaluation;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>
/// A41 qua pipeline HTTP (TestServer, InMemory): chấm thử prompt nháp, giới hạn lượt, nội dung gửi Gemini, số đo prompt.
/// AI giả (IAiEvaluator kịch bản) hoặc GeminiEvaluationService với HttpMessageHandler giả: không gọi mạng, không tốn quota thật.
/// </summary>
public class A41PromptToolsEndpointTests
{
    private const int BackendJob = 1;
    private const string FullBackendCv = "GPA 3.2. 1.5 năm làm C#, ASP.NET Core, SQL Server, EF Core, Git, Docker.";
    private const string CandidateName = "Nguyễn Thị Lan Anh";
    private const string DraftName = "Nháp (chưa lưu)";
    private static readonly DateTime Day = new(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc);

    private static readonly string[] A10Fields =
    [
        "promptVersion", "model", "keywordScore", "aiScore", "finalScore", "scoreSource", "fitLabel", "predictedPass", "yearsExperience",
        "reasoning", "aiSucceeded", "aiErrorKind", "aiErrorMessage", "aiAttempts", "aiSimulated", "aiCvTruncated", "aiMatchedSkills",
        "aiMissingSkills", "aiInvalidSkillCodes", "skills", "keywordWeight", "aiWeight", "passThreshold", "missingMustHaveCap"
    ];

    private static readonly string[] A41Fields =
        ["promptSource", "promptVersionId", "isDraft", "persisted", "remainingTries", "tryLimit", "quotaResetsAt", "tryCounted", "currentEvaluation"];

    // ---------- Hạ tầng test ----------

    /// <summary>AI giả có kịch bản: đếm số lần gọi, ghi lại request (system prompt), trả kết quả do test chọn.</summary>
    private sealed class ScriptedAi : IAiEvaluator
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentQueue<AiEvaluationRequest> Requests { get; } = new();
        public Func<AiEvaluationRequest, CancellationToken, Task<AiEvaluationResult>> Respond { get; set; } = (_, _) => Task.FromResult(Success());

        public async Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Requests.Enqueue(request);
            return await Respond(request, cancellationToken);
        }

        public static AiEvaluationResult Success() => new()
        {
            IsSuccess = true, Score = 80, MatchedSkillCodes = ["csharp"], MissingSkillCodes = [], Reasoning = "Phù hợp (giả lập).",
            Attempts = 1, IsSimulated = true
        };
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Env(WebApplicationFactory<Program> Factory, HttpClient Client, ScriptedAi? Ai) : IDisposable
    {
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }

        public async Task<T> DbAsync<T>(Func<SmartCvDbContext, Task<T>> action)
        {
            using var scope = Factory.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
        }

        public Task<int> AddPromptAsync(string name, string text, bool active) => DbAsync(async db =>
        {
            var prompt = new PromptVersion { VersionName = name, SystemPrompt = text, IsActive = active, CreatedAt = Day };
            db.PromptVersions.Add(prompt);
            await db.SaveChangesAsync();
            return prompt.PromptVersionId;
        });

        public Task<int> AddApplicationAsync(string? text = FullBackendCv, string status = ExtractionStatuses.Completed,
            string? candidate = null, bool anonymized = false, int jobId = BackendJob) => DbAsync(async db =>
        {
            var id = Guid.NewGuid();
            db.Cvs.Add(new Cv
            {
                CvId = id, OriginalFileName = "CV.pdf", StoredPath = $"Uploads/CV/2026/11/{id}.pdf", ContentType = "application/pdf",
                FileSizeBytes = 1, FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = status,
                ExtractedText = text, UploadedAt = Day, AnonymizedAt = anonymized ? Day : null,
                Candidate = candidate is null ? null : new Candidate { FullName = candidate, CreatedAt = Day }
            });
            var application = new Application { CvId = id, JobId = jobId, CreatedAt = Day };
            db.Applications.Add(application);
            await db.SaveChangesAsync();
            return application.ApplicationId;
        });

        /// <summary>Số dòng và nội dung các bảng A41 không được ghi (PromptVersions, EvaluationRuns, Evaluations, EvaluationSkills, HrLabels, ApplicationEvents).</summary>
        public Task<string> SnapshotAsync() => DbAsync(async db =>
        {
            var prompts = await db.PromptVersions.AsNoTracking().OrderBy(p => p.PromptVersionId)
                .Select(p => $"{p.PromptVersionId}|{p.VersionName}|{p.IsActive}|{p.SystemPrompt.Length}").ToListAsync();
            var evaluations = await db.Evaluations.AsNoTracking().OrderBy(e => e.EvaluationId)
                .Select(e => $"{e.EvaluationId}|{e.Status}|{e.FinalScore}|{e.PredictedPass}").ToListAsync();
            return string.Join(";", prompts.Concat(evaluations).Append(string.Join(",",
                await db.PromptVersions.CountAsync(), await db.EvaluationRuns.CountAsync(), await db.Evaluations.CountAsync(),
                await db.EvaluationSkills.CountAsync(), await db.HrLabels.CountAsync(), await db.ApplicationEvents.CountAsync())));
        });

        public Task<HttpResponseMessage> PreviewAsync(int id, object? body = null) => body is null
            ? Client.PostAsync($"/api/applications/{id}/ai-preview", null)
            : Client.PostAsJsonAsync($"/api/applications/{id}/ai-preview", body);

        public Task<HttpResponseMessage> PreviewRawAsync(int id, string json) =>
            Client.PostAsync($"/api/applications/{id}/ai-preview", new StringContent(json, Encoding.UTF8, "application/json"));

        public Task<HttpResponseMessage> AssembledAsync(object body) => Client.PostAsJsonAsync("/api/prompts/assembled-preview", body);

        public async Task<JsonElement> QuotaAsync()
        {
            using var response = await Client.GetAsync("/api/applications/ai-preview/quota");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await JsonAsync(response);
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> OkJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Env> CreateAsync(Dictionary<string, string?>? settings = null, ScriptedAi? ai = null,
        Action<IServiceCollection>? services = null, string environment = "Development")
    {
        var all = new Dictionary<string, string?>
        {
            ["Gemini:UseFake"] = "true", ["Gemini:FakeDelayMs"] = "0", ["Gemini:PreviewPermitPerMinute"] = "1000",
            ["Gemini:MinDelayBetweenCallsMs"] = "0", ["Scoring:WorkerEnabled"] = "false"
        };
        foreach (var (key, value) in settings ?? []) all[key] = value;
        var factory = TestSupport.ApiFactory(environment, all).WithWebHostBuilder(b =>
        {
            b.ConfigureLogging(l => l.ClearProviders());
            b.ConfigureTestServices(s =>
            {
                if (ai is not null) s.AddSingleton<IAiEvaluator>(ai);
                services?.Invoke(s);
            });
        });
        var env = new Env(factory, factory.CreateClient(), ai);
        await env.DbAsync(db => db.Database.EnsureCreatedAsync());
        return env;
    }

    /// <summary>Gemini THẬT (GeminiEvaluationService) với HTTP giả ghi lại payload.</summary>
    private static Task<Env> RealGeminiAsync(GeminiPayloadCapture handler, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["Gemini:UseFake"] = "false" };
        foreach (var (key, value) in extra ?? []) settings[key] = value;
        return CreateAsync(settings, services: s => s.AddHttpClient<GeminiEvaluationService>().ConfigurePrimaryHttpMessageHandler(() => handler));
    }

    // =====================================================================================
    // 1. Chấm thử: tương thích A10, prompt nháp, phiên bản đã lưu, không ghi database
    // =====================================================================================

    public static TheoryData<string?> EmptyBodies => new() { null, "{}", "null", """{"systemPrompt":null,"promptVersionId":null}""" };

    [Theory]
    [MemberData(nameof(EmptyBodies))]
    public async Task Without_prompt_choice_uses_active_prompt_and_keeps_every_a10_field(string? json)
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(ai: ai);
        var active = await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();

        using var response = json is null ? await env.PreviewAsync(id) : await env.PreviewRawAsync(id, json);
        var r = await OkJsonAsync(response);

        foreach (var field in A10Fields.Concat(A41Fields)) Assert.True(r.TryGetProperty(field, out _), $"thiếu trường {field}");
        Assert.False(r.TryGetProperty("aiRawResponse", out _));
        Assert.Equal("v-active", r.GetProperty("promptVersion").GetString());
        Assert.Equal("Active", r.GetProperty("promptSource").GetString());
        Assert.Equal(active, r.GetProperty("promptVersionId").GetInt32());
        Assert.False(r.GetProperty("isDraft").GetBoolean());
        Assert.False(r.GetProperty("persisted").GetBoolean());
        Assert.True(r.GetProperty("tryCounted").GetBoolean());
        Assert.Equal((29, 30), (r.GetProperty("remainingTries").GetInt32(), r.GetProperty("tryLimit").GetInt32()));
        Assert.Equal(JsonValueKind.Null, r.GetProperty("currentEvaluation").ValueKind);
        Assert.Equal("Bạn là HR.", Assert.Single(ai.Requests).SystemPrompt);
    }

    [Fact]
    public async Task Draft_prompt_is_trimmed_sent_to_ai_and_writes_nothing()
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(ai: ai);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();
        await SeedSavedResultsAsync(env, id);
        const string draft = "  Bạn là chuyên viên tuyển dụng 🎯. Ưu tiên kinh nghiệm thực tế; chấm khắt khe kỹ năng BẮT BUỘC.\r\n\t ";
        var before = await env.SnapshotAsync();

        var r = await OkJsonAsync(await env.PreviewAsync(id, new { systemPrompt = draft }));

        Assert.Equal(DraftName, r.GetProperty("promptVersion").GetString());
        Assert.Equal("Draft", r.GetProperty("promptSource").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("promptVersionId").ValueKind);
        Assert.True(r.GetProperty("isDraft").GetBoolean());
        Assert.Equal(draft.Trim(), Assert.Single(ai.Requests).SystemPrompt);
        Assert.Equal(before, await env.SnapshotAsync());
        var activePrompt = await OkJsonAsync(await env.Client.GetAsync("/api/prompts/active"));
        Assert.Equal("v-active", activePrompt.GetProperty("versionName").GetString());
    }

    [Fact]
    public async Task Saved_version_is_used_even_when_it_is_not_active()
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(ai: ai);
        var old = await env.AddPromptAsync("v-cu", "Prompt phiên bản cũ.", active: false);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();

        var r = await OkJsonAsync(await env.PreviewAsync(id, new { promptVersionId = old }));

        Assert.Equal("v-cu", r.GetProperty("promptVersion").GetString());
        Assert.Equal("Version", r.GetProperty("promptSource").GetString());
        Assert.Equal(old, r.GetProperty("promptVersionId").GetInt32());
        Assert.False(r.GetProperty("isDraft").GetBoolean());
        Assert.Equal("Prompt phiên bản cũ.", Assert.Single(ai.Requests).SystemPrompt);
    }

    public static TheoryData<string, string> InvalidSources => new()
    {
        { """{"systemPrompt":"Bạn là HR.","promptVersionId":1}""", "Chỉ chọn một nguồn prompt" },
        { """{"systemPrompt":""}""", "Prompt nháp đang trống" },
        { """{"systemPrompt":"   \n\t  "}""", "Prompt nháp đang trống" },
        { """{"promptVersionId":0}""", "Mã phiên bản không hợp lệ" },
        { """{"promptVersionId":-5}""", "Mã phiên bản không hợp lệ" }
    };

    [Theory]
    [MemberData(nameof(InvalidSources))]
    public async Task Invalid_prompt_choice_is_400_without_ai_call_or_lost_try(string json, string title)
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(ai: ai);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();

        using var response = await env.PreviewRawAsync(id, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(title, (await JsonAsync(response)).GetProperty("title").GetString());
        Assert.Equal(0, ai.Calls);
        Assert.Equal(30, (await env.QuotaAsync()).GetProperty("remaining").GetInt32());
    }

    public static TheoryData<string, byte[], string, HttpStatusCode, string> BrokenBodies => new()
    {
        { "ai-preview", Encoding.UTF8.GetBytes("""{"systemPrompt": "thiếu ngoặc" """), "application/json", HttpStatusCode.BadRequest, "JSON không hợp lệ" },
        { "ai-preview", Encoding.UTF8.GetBytes("""{"promptVersionId":"ba"}"""), "application/json", HttpStatusCode.BadRequest, "JSON không hợp lệ" },
        // Chữ tiếng Việt gửi theo code page Windows-1258/1252 (curl trong Git Bash, PowerShell 5.1 không đổi sang UTF-8)
        { "ai-preview", [.. "{\"systemPrompt\":\"B"u8, 0xE0, .. "n nh"u8, 0xE1, .. "p\"}"u8], "application/json", HttpStatusCode.BadRequest, "JSON không hợp lệ" },
        { "ai-preview", Encoding.UTF8.GetBytes("Bạn là HR."), "text/plain", HttpStatusCode.UnsupportedMediaType, "Nội dung yêu cầu phải là JSON" },
        { "assembled-preview", Encoding.UTF8.GetBytes("""{"applicationId":"năm"}"""), "application/json", HttpStatusCode.BadRequest, "JSON không hợp lệ" },
        { "assembled-preview", [.. "{\"applicationId\":1,\"systemPrompt\":\"B"u8, 0xE0, .. "n\"}"u8], "application/json", HttpStatusCode.BadRequest, "JSON không hợp lệ" },
        { "assembled-preview", Encoding.UTF8.GetBytes("applicationId=1"), "application/x-www-form-urlencoded", HttpStatusCode.UnsupportedMediaType, "Nội dung yêu cầu phải là JSON" }
    };

    [Theory]
    [MemberData(nameof(BrokenBodies))]
    public async Task Broken_body_gets_a_vietnamese_problem_without_stack_trace_ai_call_or_lost_try(
        string endpoint, byte[] body, string contentType, HttpStatusCode status, string title)
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(ai: ai);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        using var response = await env.Client.PostAsync(
            endpoint == "ai-preview" ? $"/api/applications/{id}/ai-preview" : "/api/prompts/assembled-preview", content);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(title, JsonDocument.Parse(text).RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("thiếu ngoặc", text);
        Assert.Equal(0, ai.Calls);
        Assert.Equal(30, (await env.QuotaAsync()).GetProperty("remaining").GetInt32());
    }

    [Theory]
    [InlineData(20_000, 0, HttpStatusCode.OK)]
    [InlineData(20_000, 5, HttpStatusCode.OK)]     // khoảng trắng đầu/cuối không tính
    [InlineData(20_001, 0, HttpStatusCode.BadRequest)]
    public async Task Draft_length_limit_is_20000_characters_after_trimming(int length, int padding, HttpStatusCode expected)
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(ai: ai);
        var id = await env.AddApplicationAsync();
        var draft = new string(' ', padding) + new string('ệ', length) + new string('\n', padding);   // tiếng Việt: client escape ệ

        using var response = await env.PreviewAsync(id, new { systemPrompt = draft });

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
            Assert.Equal(length, Assert.Single(ai.Requests).SystemPrompt.Length);
        else
        {
            Assert.Equal("Prompt quá dài", (await JsonAsync(response)).GetProperty("title").GetString());
            Assert.Equal(0, ai.Calls);
            Assert.Equal(30, (await env.QuotaAsync()).GetProperty("remaining").GetInt32());
        }
    }

    [Fact]
    public async Task Not_found_and_conflicts_never_call_ai_or_consume_tries()
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(ai: ai);
        var ready = await env.AddApplicationAsync();
        var pending = await env.AddApplicationAsync(null, ExtractionStatuses.Pending);
        var anonymized = await env.AddApplicationAsync(anonymized: true);

        async Task Expect(HttpStatusCode status, string title, Task<HttpResponseMessage> call)
        {
            using var response = await call;
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(title, (await JsonAsync(response)).GetProperty("title").GetString());
        }

        await Expect(HttpStatusCode.Conflict, "Chưa có prompt đang dùng", env.PreviewAsync(ready));
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        await Expect(HttpStatusCode.NotFound, "Không tìm thấy hồ sơ", env.PreviewAsync(999_999, new { systemPrompt = "Nháp" }));
        await Expect(HttpStatusCode.NotFound, "Không tìm thấy prompt", env.PreviewAsync(ready, new { promptVersionId = 999_999 }));
        await Expect(HttpStatusCode.Conflict, "CV chưa có nội dung", env.PreviewAsync(pending));
        await Expect(HttpStatusCode.Conflict, "CV đã được ẩn danh hóa", env.PreviewAsync(anonymized));

        Assert.Equal(0, ai.Calls);
        Assert.Equal(30, (await env.QuotaAsync()).GetProperty("remaining").GetInt32());
    }

    // =====================================================================================
    // 1b. Kết quả đã lưu gần nhất để so cạnh nhau
    // =====================================================================================

    /// <summary>Lượt cũ (Screening), lượt mới hơn (PromptTest, có kỹ năng), lượt mới nhất còn Pending, và kết quả của hồ sơ KHÁC.</summary>
    private static Task<int> SeedSavedResultsAsync(Env env, int applicationId, int? otherApplicationId = null) => env.DbAsync(async db =>
    {
        var v0 = new PromptVersion { VersionName = "v0-luu", SystemPrompt = "Prompt v0", CreatedAt = Day.AddDays(-5) };
        var v1 = new PromptVersion { VersionName = "v1-luu", SystemPrompt = "Prompt v1", CreatedAt = Day.AddDays(-4) };
        var set = new EvaluationSet { Name = "Bộ A41", CreatedAt = Day };
        db.AddRange(v0, v1, set);
        await db.SaveChangesAsync();

        EvaluationRun Run(PromptVersion prompt, string type, DateTime at, params Evaluation[] evaluations) => new()
        {
            RunType = type, PromptVersionId = prompt.PromptVersionId, EvaluationSetId = type == RunTypes.PromptTest ? set.EvaluationSetId : null,
            ModelName = "gemini-saved", Temperature = 0.2m, KeywordWeight = 0.3m, AiWeight = 0.7m, PassThreshold = 70m,
            Status = RunStatuses.Completed, TotalItems = evaluations.Length, ProcessedItems = evaluations.Length,
            CreatedAt = at, CompletedAt = at.AddMinutes(5), Evaluations = evaluations
        };
        Evaluation Eval(int app, string status, decimal? score, DateTime at) => new()
        {
            ApplicationId = app, Status = status, FinalScore = score, AiScore = score, KeywordScore = 50m,
            PredictedPass = score is null ? null : score >= 70m, ScoreSource = score is null ? null : ScoreSources.Combined,
            FitLabel = score is null ? null : "Đã lưu", Reasoning = score is null ? null : $"Kết quả đã lưu {score}", CreatedAt = at
        };

        var chosen = Eval(applicationId, EvaluationStatuses.Completed, 85m, Day.AddHours(1));
        db.EvaluationRuns.Add(Run(v0, RunTypes.Screening, Day, Eval(applicationId, EvaluationStatuses.Completed, 60m, Day)));
        db.EvaluationRuns.Add(Run(v1, RunTypes.PromptTest, Day.AddHours(1), chosen));
        db.EvaluationRuns.Add(Run(v1, RunTypes.Screening, Day.AddHours(3), Eval(applicationId, EvaluationStatuses.Pending, null, Day.AddHours(3))));
        if (otherApplicationId is { } other)
            db.EvaluationRuns.Add(Run(v1, RunTypes.Screening, Day.AddHours(4), Eval(other, EvaluationStatuses.Completed, 99m, Day.AddHours(4))));
        await db.SaveChangesAsync();
        db.EvaluationSkills.AddRange(
            new EvaluationSkill { EvaluationId = chosen.EvaluationId, JobId = BackendJob, SkillCode = "csharp", IsMatched = true, Evidence = "C#" },
            new EvaluationSkill { EvaluationId = chosen.EvaluationId, JobId = BackendJob, SkillCode = "docker", IsMatched = false });
        db.HrLabels.Add(new HrLabel { EvaluationSetId = set.EvaluationSetId, ApplicationId = applicationId, IsPass = true, LabeledBy = "HR", LabeledAt = Day });
        db.ApplicationEvents.Add(new ApplicationEvent
        {
            ApplicationId = applicationId, FromStage = ApplicationStages.New, ToStage = ApplicationStages.Reviewing, Actor = "HR", CreatedAt = Day
        });
        await db.SaveChangesAsync();
        return chosen.EvaluationId;
    });

    [Fact]
    public async Task Current_evaluation_is_the_latest_saved_result_of_this_application_only()
    {
        using var env = await CreateAsync(ai: new ScriptedAi());
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();
        var other = await env.AddApplicationAsync();
        var fresh = await env.AddApplicationAsync();
        var chosen = await SeedSavedResultsAsync(env, id, other);
        var before = await env.SnapshotAsync();

        var r = await OkJsonAsync(await env.PreviewAsync(id, new { systemPrompt = "Bản nháp thử." }));
        var current = r.GetProperty("currentEvaluation");

        Assert.Equal(chosen, current.GetProperty("evaluationId").GetInt32());
        Assert.Equal(RunTypes.PromptTest, current.GetProperty("runType").GetString());
        Assert.Equal("v1-luu", current.GetProperty("promptVersionName").GetString());
        Assert.Equal(85m, current.GetProperty("finalScore").GetDecimal());
        Assert.Equal("Đã lưu", current.GetProperty("fitLabel").GetString());
        Assert.Equal(["csharp"], current.GetProperty("matchedSkills").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(["docker"], current.GetProperty("missingSkills").EnumerateArray().Select(e => e.GetString()!));
        Assert.EndsWith("Z", current.GetProperty("createdAt").GetString());
        Assert.NotEqual(r.GetProperty("finalScore").GetDecimal(), current.GetProperty("finalScore").GetDecimal());   // thử ≠ đã lưu
        Assert.Equal(before, await env.SnapshotAsync());   // kết quả đã lưu không bị ghi đè

        var none = await OkJsonAsync(await env.PreviewAsync(fresh));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("currentEvaluation").ValueKind);
    }

    // =====================================================================================
    // 2. Giới hạn lượt chấm thử
    // =====================================================================================

    [Fact]
    public async Task Exhausted_quota_returns_429_with_retry_after_and_never_calls_ai()
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(new() { ["AiPreview:LimitPerWindow"] = "2" }, ai);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();

        Assert.Equal(1, (await OkJsonAsync(await env.PreviewAsync(id, new { systemPrompt = "Thử 1" }))).GetProperty("remainingTries").GetInt32());
        Assert.Equal(0, (await OkJsonAsync(await env.PreviewAsync(id, new { systemPrompt = "Thử 2" }))).GetProperty("remainingTries").GetInt32());
        using var third = await env.PreviewAsync(id, new { systemPrompt = "Thử 3" });

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        var problem = await JsonAsync(third);
        Assert.Equal("Hết lượt chấm thử", problem.GetProperty("title").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.StartsWith("Đã dùng hết 2 lượt chấm thử trong 60 phút. Thử lại sau ", detail);
        Assert.Matches(@"Thử lại sau \d{2}:\d{2}( ngày \d{2}/\d{2})? \(giờ Việt Nam, UTC\+7\)\.", detail);
        Assert.Contains("AI giả lập", detail);
        Assert.Equal("PreviewQuotaExhausted", problem.GetProperty("code").GetString());
        Assert.Equal((0, 2, 60), (problem.GetProperty("remaining").GetInt32(), problem.GetProperty("limit").GetInt32(), problem.GetProperty("windowMinutes").GetInt32()));
        var retryAfter = third.Headers.RetryAfter!.Delta!.Value.TotalSeconds;
        Assert.InRange(retryAfter, 3_500, 3_600);
        Assert.Equal((int)retryAfter, problem.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal(DateTimeKind.Utc, problem.GetProperty("resetsAt").GetDateTime().Kind);
        Assert.Equal(2, ai.Calls);

        var quota = await env.QuotaAsync();
        Assert.Equal((0, 2, 60, 0, 2), (quota.GetProperty("remaining").GetInt32(), quota.GetProperty("limit").GetInt32(),
            quota.GetProperty("windowMinutes").GetInt32(), quota.GetProperty("inFlight").GetInt32(), quota.GetProperty("maxConcurrent").GetInt32()));
        Assert.True(quota.GetProperty("aiSimulated").GetBoolean());
        Assert.Equal(JsonValueKind.String, quota.GetProperty("resetsAt").ValueKind);
    }

    [Fact]
    public async Task Tries_come_back_when_the_clock_passes_the_window()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 13, 7, 0, 20, TimeSpan.Zero));
        var ai = new ScriptedAi();
        using var env = await CreateAsync(ai: ai, services: s => s.AddSingleton(new PreviewQuota(1, TimeSpan.FromMinutes(60), 2, clock)));
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();

        await OkJsonAsync(await env.PreviewAsync(id));
        using var rejected = await env.PreviewAsync(id);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3_600), rejected.Headers.RetryAfter!.Delta);
        var problem = await JsonAsync(rejected);
        // 08:00:20 UTC = 15:00:20 giờ Việt Nam, làm tròn LÊN phút để không thử lại quá sớm
        Assert.Contains("Thử lại sau 15:01 (giờ Việt Nam, UTC+7).", problem.GetProperty("detail").GetString());
        Assert.Equal(new DateTime(2026, 11, 13, 8, 0, 20, DateTimeKind.Utc), problem.GetProperty("resetsAt").GetDateTime());

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await env.PreviewAsync(id)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(1));
        var again = await OkJsonAsync(await env.PreviewAsync(id));
        Assert.Equal(0, again.GetProperty("remainingTries").GetInt32());
        Assert.Equal(2, ai.Calls);
    }

    [Fact]
    public async Task Try_is_refunded_only_when_nothing_was_sent_to_ai()
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(ai: ai);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();

        // Thiếu API key: GeminiEvaluationService trả Configuration với Attempts = 0 (chưa gửi gì)
        ai.Respond = (_, _) => Task.FromResult(AiEvaluationResult.Failure(AiErrorKind.Configuration, "Chưa cấu hình Gemini API key", 0));
        var refunded = await OkJsonAsync(await env.PreviewAsync(id));
        Assert.Equal(ScoreSources.KeywordOnly, refunded.GetProperty("scoreSource").GetString());
        Assert.False(refunded.GetProperty("tryCounted").GetBoolean());
        Assert.Equal(30, refunded.GetProperty("remainingTries").GetInt32());

        // Đã gửi nhưng Gemini quá tải sau 3 lần: vẫn tính lượt
        ai.Respond = (_, _) => Task.FromResult(AiEvaluationResult.Failure(AiErrorKind.Overloaded, "Gemini đang quá tải", 3));
        var counted = await OkJsonAsync(await env.PreviewAsync(id));
        Assert.True(counted.GetProperty("tryCounted").GetBoolean());
        Assert.Equal(29, counted.GetProperty("remainingTries").GetInt32());

        // Lỗi không lường trước: không biết đã gửi chưa → giữ lượt (thận trọng) nhưng vẫn trả chỗ chạy
        ai.Respond = (_, _) => throw new InvalidOperationException("lỗi giả lập");
        using var crashed = await env.PreviewAsync(id);
        Assert.Equal(HttpStatusCode.InternalServerError, crashed.StatusCode);
        var quota = await env.QuotaAsync();
        Assert.Equal((28, 0), (quota.GetProperty("remaining").GetInt32(), quota.GetProperty("inFlight").GetInt32()));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Overloaded")]
    [InlineData(HttpStatusCode.Forbidden, "Configuration")]
    public async Task Real_gemini_error_after_sending_keeps_the_try(HttpStatusCode status, string kind)
    {
        var handler = new GeminiPayloadCapture(_ => GeminiPayloadCapture.Json(status, """{"error":{"message":"giả lập"}}"""));
        using var env = await RealGeminiAsync(handler, new() { ["Gemini:MaxRetries"] = "0" });
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();

        var r = await OkJsonAsync(await env.PreviewAsync(id));

        Assert.Equal(kind, r.GetProperty("aiErrorKind").GetString());
        Assert.Equal(1, r.GetProperty("aiAttempts").GetInt32());
        Assert.True(r.GetProperty("tryCounted").GetBoolean());
        Assert.Equal(29, r.GetProperty("remainingTries").GetInt32());
        Assert.False((await env.QuotaAsync()).GetProperty("aiSimulated").GetBoolean());
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Busy_returns_429_without_consuming_a_try()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new ScriptedAi
        {
            Respond = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return ScriptedAi.Success();
            }
        };
        using var env = await CreateAsync(new() { ["AiPreview:MaxConcurrent"] = "1" }, ai);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();

        var first = env.PreviewAsync(id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using (var busy = await env.PreviewAsync(id))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            var problem = await JsonAsync(busy);
            Assert.Equal("PreviewBusy", problem.GetProperty("code").GetString());
            Assert.Contains("Đang chạy 1/1 lượt chấm thử cùng lúc", problem.GetProperty("detail").GetString());
            Assert.Equal(TimeSpan.FromSeconds(5), busy.Headers.RetryAfter!.Delta);
        }
        var during = await env.QuotaAsync();
        Assert.Equal((29, 1), (during.GetProperty("remaining").GetInt32(), during.GetProperty("inFlight").GetInt32()));

        release.SetResult();
        using var done = await first;
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var after = await env.QuotaAsync();
        Assert.Equal((29, 0), (after.GetProperty("remaining").GetInt32(), after.GetProperty("inFlight").GetInt32()));
        Assert.Equal(1, ai.Calls);
    }

    [Fact]
    public async Task Concurrent_requests_never_exceed_the_limit_and_rejected_ones_never_call_ai()
    {
        var ai = new ScriptedAi
        {
            Respond = async (_, ct) =>
            {
                await Task.Delay(20, ct);
                return ScriptedAi.Success();
            }
        };
        using var env = await CreateAsync(new() { ["AiPreview:LimitPerWindow"] = "10", ["AiPreview:MaxConcurrent"] = "20" }, ai);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => env.PreviewAsync(id, new { systemPrompt = $"Nháp {i}" })));
        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode));
        Assert.InRange(ok, 1, 10);
        Assert.Equal(ok, ai.Calls);
        foreach (var r in responses) r.Dispose();

        // Dùng tiếp cho tới khi hết: tổng số lượt thành công đúng bằng giới hạn
        var more = 0;
        while (true)
        {
            using var r = await env.PreviewAsync(id);
            if (r.StatusCode != HttpStatusCode.OK) { Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode); break; }
            more++;
        }
        Assert.Equal(10, ok + more);
        Assert.Equal(10, ai.Calls);
        Assert.Equal(0, (await env.QuotaAsync()).GetProperty("remaining").GetInt32());
    }

    [Theory]
    [InlineData("AiPreview:LimitPerWindow", "0")]
    [InlineData("AiPreview:LimitPerWindow", "1001")]
    [InlineData("AiPreview:WindowMinutes", "0")]
    [InlineData("AiPreview:WindowMinutes", "1441")]
    [InlineData("AiPreview:MaxConcurrent", "0")]
    [InlineData("AiPreview:MaxConcurrent", "21")]
    public void Invalid_quota_configuration_stops_startup(string key, string value)
    {
        using var factory = TestSupport.ApiFactory(settings: new() { [key] = value });
        var ex = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void Appsettings_define_the_default_quota()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var o = factory.Services.GetRequiredService<IOptions<AiPreviewOptions>>().Value;
        var quota = factory.Services.GetRequiredService<PreviewQuota>();

        Assert.Equal((30, 60, 2), (o.LimitPerWindow, o.WindowMinutes, o.MaxConcurrent));
        Assert.Same(quota, factory.Services.GetRequiredService<PreviewQuota>());   // một bộ đếm cho cả ứng dụng
        Assert.Equal((30, TimeSpan.FromMinutes(60), 2), (quota.Limit, quota.Window, quota.MaxConcurrent));
    }

    // =====================================================================================
    // 3. Nội dung gửi Gemini (assembled-preview)
    // =====================================================================================

    [Theory]
    [InlineData("Draft")]
    [InlineData("Version")]
    [InlineData("Active")]
    public async Task Assembled_preview_is_exactly_what_gemini_receives_and_is_masked(string source)
    {
        var handler = new GeminiPayloadCapture();
        using var env = await RealGeminiAsync(handler);
        var saved = await env.AddPromptAsync("v-luu", "Prompt đã lưu: chấm theo kỹ năng bắt buộc.", active: false);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync(AiPromptAssemblerTests.ClearImageCv, candidate: CandidateName);
        const string draft = "  Bản nháp A41: Không trừ điểm vì thông tin bị che.  ";
        object assembledBody = source switch
        {
            "Draft" => new { applicationId = id, systemPrompt = draft },
            "Version" => new { applicationId = id, promptVersionId = saved },
            _ => new { applicationId = id }
        };
        object? previewBody = source switch
        {
            "Draft" => new { systemPrompt = draft },
            "Version" => new { promptVersionId = saved },
            _ => null
        };
        var before = await env.SnapshotAsync();

        using var assembledResponse = await env.AssembledAsync(assembledBody);
        var text = await assembledResponse.Content.ReadAsStringAsync();
        var a = await OkJsonAsync(assembledResponse);
        Assert.Empty(handler.Bodies);                                                  // không gọi Gemini
        Assert.Equal(30, (await env.QuotaAsync()).GetProperty("remaining").GetInt32());   // không tốn lượt
        Assert.Equal("no-store", assembledResponse.Headers.CacheControl?.ToString());

        await OkJsonAsync(await env.PreviewAsync(id, previewBody));
        var body = Assert.Single(handler.Bodies);

        Assert.Equal(GeminiPayloadCapture.SystemText(body), a.GetProperty("systemPrompt").GetString());
        Assert.Equal(GeminiPayloadCapture.UserText(body), a.GetProperty("userPrompt").GetString());
        var schema = JsonNode.Parse(body)!["generationConfig"]!["responseSchema"]!["properties"]!["matchedSkills"]!["items"]!["enum"]!
            .AsArray().Select(n => n!.GetValue<string>());
        Assert.Equal(schema, a.GetProperty("allowedSkillCodes").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(source, a.GetProperty("promptSource").GetString());
        Assert.Equal(source == "Draft", a.GetProperty("isDraft").GetBoolean());
        Assert.Equal(source switch { "Draft" => DraftName, "Version" => "v-luu", _ => "v-active" }, a.GetProperty("promptVersion").GetString());
        if (source == "Draft") Assert.Equal(draft.Trim(), a.GetProperty("systemPrompt").GetString());

        var user = a.GetProperty("userPrompt").GetString()!;
        foreach (var token in new[] { "[CANDIDATE]", "[EMAIL]", "[PHONE]" }) Assert.Contains(token, user);
        // Kiểm tra cả JSON thô và chuỗi đã giải mã (JSON escape tiếng Việt thành \uXXXX)
        foreach (var raw in new[] { "lananh.nguyen", "0938 214 657", "NGUYỄN THỊ LAN ANH", "Nguyễn Thị Lan Anh", "Lan Anh" })
        {
            Assert.DoesNotContain(raw, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(raw, user, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(raw, GeminiPayloadCapture.UserText(body), StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(a.GetProperty("personalDataMasked").GetBoolean());
        Assert.Equal((1, 1), (a.GetProperty("maskedEmails").GetInt32(), a.GetProperty("maskedPhones").GetInt32()));
        Assert.True(a.GetProperty("maskedNames").GetInt32() >= 1);
        Assert.Equal(a.GetProperty("maskedNames").GetInt32() + a.GetProperty("maskedEmails").GetInt32() + a.GetProperty("maskedPhones").GetInt32()
                     + a.GetProperty("maskedLinks").GetInt32() + a.GetProperty("maskedBirthDates").GetInt32() + a.GetProperty("maskedGenders").GetInt32(),
            a.GetProperty("maskedTotal").GetInt32());
        Assert.Equal("Provided", a.GetProperty("nameMaskSource").GetString());
        Assert.Equal(AiPromptAssemblerTests.ClearImageCv.Length, a.GetProperty("cvCharacters").GetInt32());
        Assert.False(a.GetProperty("truncated").GetBoolean());
        Assert.Equal(12_000, a.GetProperty("maxCvCharacters").GetInt32());
        Assert.Equal("gemini-3.1-flash-lite", a.GetProperty("model").GetString());
        Assert.False(a.GetProperty("aiSimulated").GetBoolean());
        Assert.Equal("Backend Developer (.NET)", a.GetProperty("jobTitle").GetString());
        Assert.Equal(before, await env.SnapshotAsync());
    }

    [Fact]
    public async Task Assembled_preview_fails_closed_when_masking_is_disabled()
    {
        using var env = await CreateAsync(new() { ["Gemini:MaskPersonalData"] = "false" }, new ScriptedAi());
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync(AiPromptAssemblerTests.ClearImageCv, candidate: CandidateName);

        using var response = await env.AssembledAsync(new { applicationId = id });
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PersonalDataMaskingDisabled", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("lananh.nguyen", text);
        Assert.DoesNotContain("0938", text);
    }

    [Fact]
    public async Task Assembled_preview_validates_input_like_ai_preview()
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(ai: ai);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var ready = await env.AddApplicationAsync();
        var pending = await env.AddApplicationAsync(null, ExtractionStatuses.Pending);
        var anonymized = await env.AddApplicationAsync(anonymized: true);

        async Task Expect(HttpStatusCode status, string title, Task<HttpResponseMessage> call)
        {
            using var response = await call;
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(title, (await JsonAsync(response)).GetProperty("title").GetString());
        }

        await Expect(HttpStatusCode.BadRequest, "Thiếu nội dung yêu cầu", env.Client.PostAsync("/api/prompts/assembled-preview", null));
        await Expect(HttpStatusCode.BadRequest, "Mã hồ sơ không hợp lệ", env.AssembledAsync(new { applicationId = 0 }));
        await Expect(HttpStatusCode.BadRequest, "Chỉ chọn một nguồn prompt", env.AssembledAsync(new { applicationId = ready, systemPrompt = "x", promptVersionId = 1 }));
        await Expect(HttpStatusCode.BadRequest, "Prompt nháp đang trống", env.AssembledAsync(new { applicationId = ready, systemPrompt = " " }));
        await Expect(HttpStatusCode.BadRequest, "Prompt quá dài", env.AssembledAsync(new { applicationId = ready, systemPrompt = new string('a', 20_001) }));
        await Expect(HttpStatusCode.NotFound, "Không tìm thấy hồ sơ", env.AssembledAsync(new { applicationId = 999_999 }));
        await Expect(HttpStatusCode.NotFound, "Không tìm thấy prompt", env.AssembledAsync(new { applicationId = ready, promptVersionId = 999_999 }));
        await Expect(HttpStatusCode.Conflict, "CV chưa có nội dung", env.AssembledAsync(new { applicationId = pending }));
        await Expect(HttpStatusCode.Conflict, "CV đã được ẩn danh hóa", env.AssembledAsync(new { applicationId = anonymized }));

        Assert.Equal(0, ai.Calls);
        Assert.Equal(30, (await env.QuotaAsync()).GetProperty("remaining").GetInt32());
    }

    // =====================================================================================
    // Bảo vệ: chỉ Development + CvUpload:Endpoint:Enabled, giữ rate limiter A10, giới hạn body
    // =====================================================================================

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Prompt_tools_do_not_exist_outside_development(string environment)
    {
        using var factory = TestSupport.ApiFactory(environment);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/applications/ai-preview/quota")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/prompts/assembled-preview", new { applicationId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/applications/1/ai-preview", new { systemPrompt = "x" })).StatusCode);
    }

    [Fact]
    public async Task Prompt_tools_do_not_exist_when_development_endpoints_are_disabled()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["CvUpload:Endpoint:Enabled"] = "false", ["Gemini:UseFake"] = "true" });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/applications/ai-preview/quota")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/prompts/assembled-preview", new { applicationId = 1 })).StatusCode);
    }

    [Fact]
    public void Endpoints_keep_a10_rate_limit_and_limit_request_body()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Gemini:UseFake"] = "true" });
        using var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        RouteEndpoint Find(string pattern) => endpoints.Single(e => e.RoutePattern.RawText == pattern);

        var preview = Find("/api/applications/{id:int}/ai-preview");
        Assert.Equal(AiPreviewRateLimitPolicyName, preview.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal((long?)(256 * 1024), preview.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
        Assert.Equal((long?)(256 * 1024), Find("/api/prompts/assembled-preview").Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
        Assert.Contains("GET", Find("/api/applications/ai-preview/quota").Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
    }

    private const string AiPreviewRateLimitPolicyName = "ai-preview";

    [Fact]
    public async Task A10_ip_rate_limit_still_applies_before_the_quota()
    {
        var ai = new ScriptedAi();
        using var env = await CreateAsync(new() { ["Gemini:PreviewPermitPerMinute"] = "1" }, ai);
        await env.AddPromptAsync("v-active", "Bạn là HR.", active: true);
        var id = await env.AddApplicationAsync();

        await OkJsonAsync(await env.PreviewAsync(id));
        using var limited = await env.PreviewAsync(id);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("Quá nhiều yêu cầu chấm thử", (await JsonAsync(limited)).GetProperty("title").GetString());
        Assert.Equal(1, ai.Calls);
        Assert.Equal(29, (await env.QuotaAsync()).GetProperty("remaining").GetInt32());   // bị chặn theo IP thì không tốn lượt
    }

    // =====================================================================================
    // 4. Số đo prompt: ma trận 2×2, khoảng tin cậy Wilson, mức độ kết luận
    // =====================================================================================

    private static async Task<(Env Env, PromptCompareSeed.Data Data)> DemoAsync()
    {
        var env = await CreateAsync();
        var data = await env.DbAsync(PromptCompareSeed.DemoAsync);
        return (env, data);
    }

    private static void AssertMatrix(JsonElement matrix, int truePass, int missed, int wrongPick, int trueFail, int unscored)
    {
        Assert.Equal((truePass, missed, wrongPick, trueFail, unscored), (matrix.GetProperty("truePass").GetInt32(), matrix.GetProperty("missed").GetInt32(),
            matrix.GetProperty("wrongPick").GetInt32(), matrix.GetProperty("trueFail").GetInt32(), matrix.GetProperty("unscored").GetInt32()));
        Assert.Equal(truePass + missed + wrongPick + trueFail + unscored, matrix.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Prompt_test_list_and_detail_expose_matrix_and_wilson_interval()
    {
        var (env, data) = await DemoAsync();
        using var _ = env;

        var list = await OkJsonAsync(await env.Client.GetAsync($"/api/prompt-tests?evaluationSetId={data.Set}"));
        var v1 = list.EnumerateArray().Single(r => r.GetProperty("runId").GetInt32() == data.RunV1);
        var v2 = list.EnumerateArray().Single(r => r.GetProperty("runId").GetInt32() == data.RunV2);
        var detail = await OkJsonAsync(await env.Client.GetAsync($"/api/prompt-tests/{data.RunV1}"));

        foreach (var run in new[] { v1, detail })
        {
            Assert.Equal(70m, run.GetProperty("accuracyPercent").GetDecimal());
            Assert.Equal((39.68m, 89.22m), (run.GetProperty("accuracyLow").GetDecimal(), run.GetProperty("accuracyHigh").GetDecimal()));
            AssertMatrix(run.GetProperty("matrix"), 5, 1, 2, 2, 0);
            Assert.Equal(run.GetProperty("totalLabeledCount").GetInt32(), run.GetProperty("matrix").GetProperty("total").GetInt32());
            Assert.Equal(7, run.GetProperty("correctCount").GetInt32());   // trường A16 giữ nguyên
        }
        Assert.Equal(80m, v2.GetProperty("accuracyPercent").GetDecimal());
        Assert.Equal((49.02m, 94.33m), (v2.GetProperty("accuracyLow").GetDecimal(), v2.GetProperty("accuracyHigh").GetDecimal()));
        AssertMatrix(v2.GetProperty("matrix"), 5, 1, 1, 3, 0);
    }

    [Fact]
    public async Task Failed_pending_and_keyword_only_results_stay_in_the_matrix_total()
    {
        var (env, data) = await DemoAsync();
        using var _ = env;
        var runId = await env.DbAsync(async db =>
        {
            var run = PromptCompareSeed.Run(data.PromptV1, data.Set, data.Apps, PromptCompareSeed.V1Scores, Day, Day.AddMinutes(5));
            var items = run.Evaluations.ToList();
            items[0].Status = EvaluationStatuses.Failed;  items[0].PredictedPass = null; items[0].FinalScore = null;   // HR Đạt
            items[2].Status = EvaluationStatuses.Pending; items[2].PredictedPass = null; items[2].FinalScore = null;   // HR Không đạt
            items[3].ScoreSource = ScoreSources.KeywordOnly;                                                           // vẫn có dự đoán
            db.EvaluationRuns.Add(run);
            await db.SaveChangesAsync();
            return run.RunId;
        });
        var running = await env.DbAsync(async db =>
        {
            var run = PromptCompareSeed.Run(data.PromptV2, data.Set, data.Apps, PromptCompareSeed.V2Scores, Day.AddHours(5), null, RunStatuses.Running);
            db.EvaluationRuns.Add(run);
            await db.SaveChangesAsync();
            return run.RunId;
        });

        var detail = await OkJsonAsync(await env.Client.GetAsync($"/api/prompt-tests/{runId}"));
        AssertMatrix(detail.GetProperty("matrix"), 4, 1, 2, 1, 2);
        Assert.Equal(10, detail.GetProperty("totalLabeledCount").GetInt32());
        Assert.Equal(50m, detail.GetProperty("accuracyPercent").GetDecimal());   // 5/10: CV không chấm được vẫn ở mẫu số
        var (low, high) = PromptAccuracyCalculator.Wilson(5, 10);
        Assert.Equal((low, high), (detail.GetProperty("accuracyLow").GetDecimal(), detail.GetProperty("accuracyHigh").GetDecimal()));
        Assert.Equal(1, detail.GetProperty("keywordOnlyCount").GetInt32());

        var list = await OkJsonAsync(await env.Client.GetAsync($"/api/prompt-tests?evaluationSetId={data.Set}"));
        var fromList = list.EnumerateArray().Single(r => r.GetProperty("runId").GetInt32() == runId);
        AssertMatrix(fromList.GetProperty("matrix"), 4, 1, 2, 1, 2);
        var unfinished = list.EnumerateArray().Single(r => r.GetProperty("runId").GetInt32() == running);
        Assert.Equal(JsonValueKind.Null, unfinished.GetProperty("matrix").ValueKind);
        Assert.Equal(JsonValueKind.Null, unfinished.GetProperty("accuracyLow").ValueKind);
        Assert.Equal(JsonValueKind.Null, unfinished.GetProperty("accuracyHigh").ValueKind);
    }

    [Fact]
    public async Task Compare_endpoints_add_conclusion_without_changing_verdict_or_summary()
    {
        var (env, data) = await DemoAsync();
        using var _ = env;
        const string summary =
            "v2-tieu-chi-ro-rang đúng 80% (8/10 CV), cao hơn v1-co-ban 10 điểm phần trăm (70%, 7/10 CV). " +
            "Sửa đúng thêm 1 CV, không làm sai thêm CV nào. Bỏ sót giữ nguyên (1 → 1), chọn nhầm giảm 1 (2 → 1).";

        var byRuns = await OkJsonAsync(await env.Client.GetAsync($"/api/prompt-tests/compare?baseRunId={data.RunV1}&candidateRunId={data.RunV2}"));
        var byVersions = await OkJsonAsync(await env.Client.GetAsync(
            $"/api/prompts/compare?baseVersionId={data.PromptV1}&candidateVersionId={data.PromptV2}&evaluationSetId={data.Set}"));

        foreach (var c in new[] { byRuns, byVersions })
        {
            Assert.Equal(CompareVerdicts.Better, c.GetProperty("verdict").GetString());
            Assert.Equal(summary, c.GetProperty("summary").GetString());
            Assert.False(c.GetProperty("conclusive").GetBoolean());
            Assert.StartsWith(PromptComparer.InconclusiveText, c.GetProperty("conclusionNote").GetString());
            Assert.Contains("chưa đủ cơ sở kết luận", c.GetProperty("conclusionNote").GetString());
            Assert.Equal((39.68m, 89.22m), (c.GetProperty("base").GetProperty("accuracyLow").GetDecimal(), c.GetProperty("base").GetProperty("accuracyHigh").GetDecimal()));
            Assert.Equal((49.02m, 94.33m), (c.GetProperty("candidate").GetProperty("accuracyLow").GetDecimal(), c.GetProperty("candidate").GetProperty("accuracyHigh").GetDecimal()));
            AssertMatrix(c.GetProperty("base").GetProperty("matrix"), 5, 1, 2, 2, 0);
            AssertMatrix(c.GetProperty("candidate").GetProperty("matrix"), 5, 1, 1, 3, 0);
        }
    }
}
