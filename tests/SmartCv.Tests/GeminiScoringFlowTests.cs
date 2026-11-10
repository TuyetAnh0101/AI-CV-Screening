extern alias web;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Data;
using RunFlow = web::SmartCv.Web.Helpers.RunFlow;

namespace SmartCv.Tests;

/// <summary>
/// Gemini THẬT (GeminiEvaluationService) + worker THẬT + API THẬT, chỉ HTTP tới Google là giả (không gọi mạng, không tốn quota).
/// Tái hiện các lỗi thật: 503 "high demand" (lượt #16), 404 model bị gỡ (lượt #13).
/// </summary>
public class GeminiScoringFlowTests
{
    private const int BackendJob = 1;
    private const string FullCv = "GPA 3.2. 1.5 năm làm C#, ASP.NET Core, SQL Server, EF Core, Git, Docker.";
    private const string HighDemand = "This model is currently experiencing high demand. Spikes in demand are usually temporary. Please try again later.";

    /// <summary>Trả lời theo số thứ tự lần gọi (1, 2, ...). Đếm số lần gọi và model đã gọi.</summary>
    private sealed class ScriptedGemini(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref calls);
            lock (Urls) Urls.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(n));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Success(int score) => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
    {
        candidates = new[]
        {
            new
            {
                finishReason = "STOP",
                content = new { parts = new[] { new { text = $$"""{"score":{{score}},"matchedSkills":[],"missingSkills":[],"yearsExperience":1.5,"gpa":3.2,"reasoning":"Phù hợp."}""" } } }
            }
        }
    }));

    private static HttpResponseMessage Overloaded() =>
        Json(HttpStatusCode.ServiceUnavailable, $$$"""{"error":{"code":503,"message":"{{{HighDemand}}}","status":"UNAVAILABLE"}}""");

    private static HttpResponseMessage RetiredModel() =>
        Json(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"This model models/gemini-2.5-flash is no longer available to new users.","status":"NOT_FOUND"}}""");

    private static WebApplicationFactory<Program> Host(ScriptedGemini gemini) =>
        TestSupport.ApiFactory(settings: new()
        {
            ["Scoring:WorkerEnabled"] = "true", ["Scoring:PollIntervalSeconds"] = "1",
            ["Gemini:UseFake"] = "false", ["Gemini:MinDelayBetweenCallsMs"] = "0",
            ["Gemini:MaxRetryDelaySeconds"] = "0"   // thử lại ngay, test không phải chờ 2 s, 4 s
        }).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient<GeminiEvaluationService>().ConfigurePrimaryHttpMessageHandler(() => gemini)));

    private static async Task<int> SeedRunAsync(WebApplicationFactory<Program> factory, int cvCount, string model = "gemini-3.1-flash-lite")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        var prompt = new PromptVersion { VersionName = "v", SystemPrompt = "HR", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.PromptVersions.Add(prompt);
        await db.SaveChangesAsync();
        var run = new EvaluationRun
        {
            RunType = RunTypes.Screening, PromptVersionId = prompt.PromptVersionId, ModelName = model, Temperature = 0.2m,
            KeywordWeight = 0.3m, AiWeight = 0.7m, PassThreshold = 70m, TotalItems = cvCount, CreatedAt = DateTime.UtcNow
        };
        for (var i = 0; i < cvCount; i++)
        {
            var cvId = Guid.NewGuid();
            db.Cvs.Add(new Cv
            {
                CvId = cvId, OriginalFileName = $"cv-{i}.pdf", StoredPath = $"Uploads/CV/{cvId}.pdf", ContentType = "application/pdf", FileSizeBytes = 1,
                FileHash = cvId.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = ExtractionStatuses.Completed,
                ExtractedText = FullCv, UploadedAt = DateTime.UtcNow
            });
            var application = new Application { CvId = cvId, JobId = BackendJob, CreatedAt = DateTime.UtcNow };
            db.Applications.Add(application);
            run.Evaluations.Add(new Evaluation { Application = application, CreatedAt = DateTime.UtcNow });
        }
        db.EvaluationRuns.Add(run);
        await db.SaveChangesAsync();
        return run.RunId;
    }

    private static async Task<RunProgressDto> WaitCompletedAsync(HttpClient client, int runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var run = (await client.GetFromJsonAsync<RunProgressDto>($"/api/runs/{runId}"))!;
            if (run.Status == RunStatuses.Completed) return run;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{run.Status} {run.ProcessedItems}/{run.TotalItems}");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Overloaded_then_success_saves_ai_score_and_combined_final_score_once_per_cv()
    {
        var gemini = new ScriptedGemini(n => n == 1 ? Overloaded() : Success(60));   // lần gọi đầu 503, sau đó ổn
        using var factory = Host(gemini);
        using var client = factory.CreateClient();
        var runId = await SeedRunAsync(factory, 3);

        var run = await WaitCompletedAsync(client, runId);

        Assert.Equal((3, 0, 100m), (run.ProcessedItems, run.FailedItems, run.Percent));
        Assert.All(run.Items!, i =>
        {
            Assert.Equal(EvaluationStatuses.Completed, i.Status);
            Assert.Equal(ScoreSources.Combined, i.ScoreSource);
            Assert.Equal(60m, i.AiScore);
            Assert.Equal(Math.Round(0.3m * i.KeywordScore!.Value + 0.7m * 60m, 2, MidpointRounding.AwayFromZero), i.FinalScore);   // 30% keyword + 70% AI
            Assert.Null(RunFlow.FriendlyItemError(i));   // AI thành công: không có ghi chú lỗi
        });
        Assert.Equal(4, gemini.Calls);   // 1 lần 503 + 3 CV thành công
        Assert.All(gemini.Urls, u => Assert.EndsWith("/models/gemini-3.1-flash-lite:generateContent", u));   // đúng model đã chụp của lượt

        await Task.Delay(2500);           // worker còn chạy thêm vài vòng: không chấm lại CV đã Completed
        Assert.Equal(4, gemini.Calls);
    }

    [Fact]
    public async Task Persistent_overload_falls_back_to_keyword_only_with_clear_reason_and_run_still_completes()
    {
        var gemini = new ScriptedGemini(_ => Overloaded());
        using var factory = Host(gemini);
        using var client = factory.CreateClient();
        var runId = await SeedRunAsync(factory, 2);

        var run = await WaitCompletedAsync(client, runId);

        Assert.Equal((2, 0), (run.ProcessedItems, run.FailedItems));   // KeywordOnly vẫn là Completed, lượt không bị treo
        Assert.All(run.Items!, i =>
        {
            Assert.Equal(ScoreSources.KeywordOnly, i.ScoreSource);     // không ghi Combined khi AI không chấm được
            Assert.Null(i.AiScore);                                    // không tạo điểm AI giả
            Assert.Equal(i.KeywordScore, i.FinalScore);
            Assert.Contains("Gemini đang quá tải (HTTP 503", i.ErrorMessage);
            Assert.Equal("AI chưa chấm được (Gemini đang quá tải, có thể thử lại sau), điểm chỉ tính theo từ khóa.", RunFlow.FriendlyItemError(i));
        });
        Assert.Equal(6, gemini.Calls);   // giới hạn: mỗi CV 3 lần (MaxRetries = 2), không thử vô hạn

        await Task.Delay(2500);
        Assert.Equal(6, gemini.Calls);   // không tự chấm lại hồ sơ KeywordOnly đã Completed
    }

    [Fact]
    public async Task Retired_model_is_not_retried_and_reason_points_to_model_configuration()
    {
        var gemini = new ScriptedGemini(_ => RetiredModel());
        using var factory = Host(gemini);
        using var client = factory.CreateClient();
        var runId = await SeedRunAsync(factory, 2, model: "gemini-2.5-flash");   // lượt cũ đã chụp model bị gỡ

        var run = await WaitCompletedAsync(client, runId);

        Assert.All(run.Items!, i =>
        {
            Assert.Equal(ScoreSources.KeywordOnly, i.ScoreSource);
            Assert.Contains("model không còn khả dụng", i.ErrorMessage);
            Assert.Equal("AI chưa chấm được (model Gemini không khả dụng, cần kiểm tra cấu hình model), điểm chỉ tính theo từ khóa.",
                RunFlow.FriendlyItemError(i));
        });
        Assert.Equal(2, gemini.Calls);   // 404 không thử lại: mỗi CV đúng 1 lần
        Assert.All(gemini.Urls, u => Assert.EndsWith("/models/gemini-2.5-flash:generateContent", u));   // không tự đổi model của lượt
    }
}
