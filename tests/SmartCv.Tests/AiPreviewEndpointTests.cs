using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>A10: POST /api/applications/{id}/ai-preview qua pipeline HTTP. AI giả hoặc Gemini với HTTP giả, không gọi mạng thật.</summary>
public class AiPreviewEndpointTests
{
    private const int BackendJob = 1;
    private const int AiJob = 3;
    private const string FullBackendCv = "GPA 3.2. 1.5 năm làm C#, ASP.NET Core, SQL Server, EF Core, Git, Docker.";
    private const string GiangCv = "GPA 3.10. Lập trình Python cơ bản, phân tích dữ liệu bằng Excel.";

    private static readonly Dictionary<string, string?> FakeSettings = new()
    {
        ["Gemini:UseFake"] = "true",
        ["Gemini:FakeDelayMs"] = "0"
    };

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed record Env(WebApplicationFactory<Program> Factory, HttpClient Client) : IDisposable
    {
        public void Dispose() { Client.Dispose(); Factory.Dispose(); }

        public async Task<T> DbAsync<T>(Func<SmartCvDbContext, Task<T>> action)
        {
            using var scope = Factory.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
        }

        public async Task<int> AddPromptAsync() => await DbAsync(async db =>
        {
            db.PromptVersions.Add(new PromptVersion { VersionName = "v-test", SystemPrompt = "Bạn là HR.", IsActive = true, CreatedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });

        public async Task<int> AddApplicationAsync(int jobId, string status, string? text)
        {
            var id = Guid.NewGuid();
            return await DbAsync(async db =>
            {
                db.Cvs.Add(new Cv
                {
                    CvId = id, OriginalFileName = "CV.pdf", StoredPath = $"Uploads/CV/2026/11/{id}.pdf", ContentType = "application/pdf", FileSizeBytes = 1,
                    FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = status, ExtractedText = text, UploadedAt = DateTime.UtcNow
                });
                var application = new Application { CvId = id, JobId = jobId, CreatedAt = DateTime.UtcNow };
                db.Applications.Add(application);
                await db.SaveChangesAsync();
                return application.ApplicationId;
            });
        }

        public Task<HttpResponseMessage> PreviewAsync(int id) => Client.PostAsync($"/api/applications/{id}/ai-preview", null);

        public async Task<AiPreviewDto> PreviewOkAsync(int id)
        {
            using var response = await PreviewAsync(id);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<AiPreviewDto>())!;
        }
    }

    private static async Task<Env> FromFactoryAsync(WebApplicationFactory<Program> factory, bool prompt)
    {
        var env = new Env(factory, factory.CreateClient());
        await env.DbAsync(db => db.Database.EnsureCreatedAsync());
        if (prompt) await env.AddPromptAsync();
        return env;
    }

    private static Task<Env> FakeAsync(bool prompt = true, Dictionary<string, string?>? extra = null) =>
        FromFactoryAsync(TestSupport.ApiFactory(settings: new(FakeSettings.Concat(extra ?? []))), prompt);

    /// <summary>Gemini thật (GeminiEvaluationService) với HTTP giả.</summary>
    private static async Task<(Env Env, StubHandler Handler)> StubbedGeminiAsync(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var factory = TestSupport.ApiFactory(settings: new() { ["Gemini:MinDelayBetweenCallsMs"] = "0" }).WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddHttpClient<GeminiEvaluationService>().ConfigurePrimaryHttpMessageHandler(() => handler)));
        return (await FromFactoryAsync(factory, prompt: true), handler);
    }

    // ---------- AI giả ----------

    [Fact]
    public async Task Fake_ai_full_cv_is_combined_and_labelled_as_simulated()
    {
        using var env = await FakeAsync();
        var id = await env.AddApplicationAsync(BackendJob, ExtractionStatuses.Completed, FullBackendCv);

        var r = await env.PreviewOkAsync(id);

        Assert.Equal("v-test", r.PromptVersion);
        Assert.Equal(FakeAiEvaluator.ModelLabel, r.Model);
        Assert.Equal((100m, 100m, 100m), (r.KeywordScore, r.AiScore!.Value, r.FinalScore));
        Assert.Equal(ScoreSources.Combined, r.ScoreSource);
        Assert.Equal("Rất phù hợp", r.FitLabel);
        Assert.True(r.PredictedPass);
        Assert.Equal(1.5m, r.YearsExperience);
        Assert.True(r.AiSucceeded);
        Assert.True(r.AiSimulated);
        Assert.Equal("None", r.AiErrorKind);
        Assert.StartsWith("(AI giả lập)", r.Reasoning);
        Assert.Equal(6, r.AiMatchedSkills.Count);
    }

    [Fact]
    public async Task Fake_ai_score_is_rounded_keyword_score()
    {
        using var env = await FakeAsync();
        var id = await env.AddApplicationAsync(AiJob, ExtractionStatuses.Completed, GiangCv);

        var r = await env.PreviewOkAsync(id);

        Assert.Equal(33.33m, r.KeywordScore);
        Assert.Equal(33m, r.AiScore);
        Assert.Equal(33.1m, r.FinalScore);   // 0.3 × 33.33 + 0.7 × 33
        Assert.Contains("machine_learning", r.AiMissingSkills);
    }

    [Fact]
    public async Task Fake_error_marker_gives_keyword_only()
    {
        using var env = await FakeAsync();
        var id = await env.AddApplicationAsync(BackendJob, ExtractionStatuses.Completed, FullBackendCv + " #loi-ai");

        var r = await env.PreviewOkAsync(id);

        Assert.Equal(ScoreSources.KeywordOnly, r.ScoreSource);
        Assert.Null(r.AiScore);
        Assert.Equal(100m, r.FinalScore);
        Assert.False(r.AiSucceeded);
        Assert.Equal("QuotaExceeded", r.AiErrorKind);
        Assert.True(r.AiSimulated);
        Assert.Contains("điểm chỉ tính theo từ khóa", r.Reasoning);
    }

    [Fact]
    public async Task Fake_mode_resolves_fake_evaluator_and_needs_no_key()
    {
        using var factory = TestSupport.ApiFactory(settings: new(FakeSettings) { ["Gemini:ApiKey"] = "" });
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        Assert.IsType<FakeAiEvaluator>(scope.ServiceProvider.GetRequiredService<IAiEvaluator>());
    }

    [Fact]
    public async Task Preview_writes_nothing()
    {
        using var env = await FakeAsync();
        var id = await env.AddApplicationAsync(BackendJob, ExtractionStatuses.Completed, FullBackendCv);

        await env.PreviewOkAsync(id);

        Assert.Equal(0, await env.DbAsync(db => db.Evaluations.CountAsync()));
        Assert.Equal(0, await env.DbAsync(db => db.EvaluationSkills.CountAsync()));
        Assert.Equal(0, await env.DbAsync(db => db.EvaluationRuns.CountAsync()));
    }

    // ---------- Gemini với HTTP giả ----------

    private static string Envelope(string json) =>
        JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = json } } } } } });

    [Fact]
    public async Task Gemini_success_combines_scores_and_hides_raw_response()
    {
        const string json = """{"score":60,"matchedSkills":["python","machine_learning"],"missingSkills":["llm_api"],"yearsExperience":null,"gpa":3.1,"reasoning":"LY-DO"}""";
        var (env, handler) = await StubbedGeminiAsync(HttpStatusCode.OK, Envelope(json));
        using var _ = env;
        var id = await env.AddApplicationAsync(AiJob, ExtractionStatuses.Completed, GiangCv);

        using var response = await env.PreviewAsync(id);
        var text = await response.Content.ReadAsStringAsync();
        var r = JsonSerializer.Deserialize<AiPreviewDto>(text, JsonSerializerOptions.Web)!;

        Assert.Equal(1, handler.Calls);
        Assert.Equal(ScoreSources.Combined, r.ScoreSource);
        Assert.Equal(52m, r.FinalScore);   // 0.3 × 33.33 + 0.7 × 60 = 52.00, thiếu llm_api nhưng dưới mức giới hạn
        Assert.Equal("gemini-3.1-flash-lite", r.Model);   // Scoring:ModelName trong appsettings.json
        Assert.False(r.AiSimulated);
        Assert.Equal(SkillMatchSourcesAi, r.Skills.Single(s => s.SkillCode == "machine_learning").MatchedBy);
        Assert.DoesNotContain("rawResponse", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("matchedSkills\":[\"python", text);
    }

    private const string SkillMatchSourcesAi = "AI";

    [Fact]
    public async Task Gemini_invalid_key_gives_keyword_only_after_one_call()
    {
        var (env, handler) = await StubbedGeminiAsync(HttpStatusCode.Forbidden, """{"error":{"message":"denied"}}""");
        using var _ = env;
        var id = await env.AddApplicationAsync(BackendJob, ExtractionStatuses.Completed, FullBackendCv);

        var r = await env.PreviewOkAsync(id);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(ScoreSources.KeywordOnly, r.ScoreSource);
        Assert.Equal("Configuration", r.AiErrorKind);
        Assert.Equal(1, r.AiAttempts);
        Assert.Contains("Không có quyền", r.AiErrorMessage);
    }

    [Fact]
    public async Task Default_configuration_resolves_real_gemini_service()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        Assert.IsType<GeminiEvaluationService>(scope.ServiceProvider.GetRequiredService<IAiEvaluator>());
    }

    // ---------- Trường hợp không chấm được ----------

    [Fact]
    public async Task Unknown_application_returns_404()
    {
        using var env = await FakeAsync();
        using var response = await env.PreviewAsync(999_999);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(ExtractionStatuses.Pending, null)]
    [InlineData(ExtractionStatuses.Failed, null)]
    [InlineData(ExtractionStatuses.Completed, "  ")]
    [InlineData(ExtractionStatuses.Pending, "Python chữ cũ")]
    public async Task Cv_not_ready_returns_409(string status, string? text)
    {
        using var env = await FakeAsync();
        var id = await env.AddApplicationAsync(AiJob, status, text);

        using var response = await env.PreviewAsync(id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status, json.RootElement.GetProperty("extractionStatus").GetString());
    }

    [Fact]
    public async Task No_active_prompt_returns_409_without_calling_ai()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var factory = TestSupport.ApiFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddHttpClient<GeminiEvaluationService>().ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var env = await FromFactoryAsync(factory, prompt: false);
        var id = await env.AddApplicationAsync(AiJob, ExtractionStatuses.Completed, GiangCv);

        using var response = await env.PreviewAsync(id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Chưa có prompt đang dùng", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, handler.Calls);
    }

    // ---------- Bảo vệ ----------

    [Fact]
    public async Task Requests_over_the_limit_get_429()
    {
        using var env = await FakeAsync(extra: new() { ["Gemini:PreviewPermitPerMinute"] = "1" });
        var id = await env.AddApplicationAsync(BackendJob, ExtractionStatuses.Completed, FullBackendCv);

        await env.PreviewOkAsync(id);
        using var second = await env.PreviewAsync(id);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains("chấm thử", await second.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Endpoint_does_not_exist_outside_development(string environment)
    {
        using var factory = TestSupport.ApiFactory(environment);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/applications/1/ai-preview", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- Cấu hình ----------

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Fake_ai_outside_development_stops_startup(string environment)
    {
        using var factory = TestSupport.ApiFactory(environment, settings: new(FakeSettings));
        var ex = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("UseFake", ex.Message);
    }

    [Fact]
    public void Missing_api_key_still_stops_startup_for_real_gemini()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Gemini:ApiKey"] = "" });
        var ex = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("Chưa cấu hình Gemini:ApiKey", ex.Message);
    }

    [Fact]
    public void Appsettings_keep_weights_cap_and_real_ai_by_default()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var o = factory.Services.GetRequiredService<IOptions<ScoringOptions>>().Value;
        var g = factory.Services.GetRequiredService<IOptions<SmartCv.Infrastructure.Gemini.GeminiOptions>>().Value;

        Assert.Equal((0.3m, 0.7m, 70m, 69m), (o.KeywordWeight, o.AiWeight, o.PassThreshold, o.MissingMustHaveCap!.Value));
        Assert.False(g.UseFake);
        Assert.Equal((2, 90, 60, 4500, 12000), (g.MaxRetries, g.TimeoutSeconds, g.MaxRetryDelaySeconds, g.MinDelayBetweenCallsMs, g.MaxCvCharacters));
    }

    [Fact]
    public void Null_cap_in_configuration_disables_the_rule()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:MissingMustHaveCap"] = null });
        using var client = factory.CreateClient();

        Assert.Null(factory.Services.GetRequiredService<IOptions<ScoringOptions>>().Value.MissingMustHaveCap);
    }

    [Fact]
    public void Cap_outside_score_range_stops_startup()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:MissingMustHaveCap"] = "150" });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public void Weights_not_summing_to_one_stop_startup()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:AiWeight"] = "0.5" });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
}
