extern alias web;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B39: client Web gọi API A41. (1) Hình dạng body/URL và cách đọc lỗi (code, Retry-After) với HTTP giả.
/// (2) Hợp đồng thật: Web client gọi API thật chạy trong bộ nhớ (InMemory, AI giả) — không Gemini, không SQL Server.
/// </summary>
public class B39PromptApiClientTests
{
    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string media = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, media) };

    private static (PromptApiClient Prompt, AiPreviewApiClient Preview, Recorder Http) Clients(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var recorder = new Recorder(respond);
        var http = new HttpClient(recorder, disposeHandler: false) { BaseAddress = new Uri("https://api.test/") };
        return (new PromptApiClient(http, NullLogger<PromptApiClient>.Instance), new AiPreviewApiClient(http, NullLogger<AiPreviewApiClient>.Instance), recorder);
    }

    private const string PreviewJson = """
        {"promptVersion":"Nháp (chưa lưu)","model":"fake-ai (giả lập)","keywordScore":60,"aiScore":80,"finalScore":74,
         "scoreSource":"Combined","fitLabel":"Phù hợp","predictedPass":true,"yearsExperience":null,"reasoning":"ok",
         "aiSucceeded":true,"aiErrorKind":"None","aiErrorMessage":null,"aiAttempts":1,"aiSimulated":true,"aiCvTruncated":false,
         "aiMatchedSkills":["csharp"],"aiMissingSkills":[],"aiInvalidSkillCodes":[],"skills":[],
         "promptSource":"Draft","promptVersionId":null,"isDraft":true,"persisted":false,"remainingTries":29,"tryLimit":30,
         "quotaResetsAt":"2026-11-17T08:00:00Z","tryCounted":true,"currentEvaluation":null}
        """;

    // ---------- (1) Body và URL ----------

    [Fact]
    public async Task Draft_preview_posts_only_system_prompt_to_ai_preview()
    {
        var (_, preview, http) = Clients(_ => Json(HttpStatusCode.OK, PreviewJson));
        var result = await preview.PreviewAsync(5, new AiPreviewRequest { SystemPrompt = "Bạn là HR.\nQuy tắc mới." });

        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.IsDraft);
        Assert.Equal(29, result.Data.RemainingTries);
        var (method, path, body) = Assert.Single(http.Requests);
        Assert.Equal(("POST", "/api/applications/5/ai-preview"), (method, path));
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(["systemPrompt"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Bạn là HR.\nQuy tắc mới.", json.RootElement.GetProperty("systemPrompt").GetString());
    }

    [Fact]
    public async Task Version_preview_posts_only_prompt_version_id()
    {
        var (_, preview, http) = Clients(_ => Json(HttpStatusCode.OK, PreviewJson));
        await preview.PreviewAsync(5, new AiPreviewRequest { PromptVersionId = 3 });
        using var json = JsonDocument.Parse(Assert.Single(http.Requests).Body!);
        Assert.Equal(["promptVersionId"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(3, json.RootElement.GetProperty("promptVersionId").GetInt32());
    }

    [Theory]
    [InlineData("nháp", 3)]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public async Task Invalid_source_is_rejected_without_any_http_call(string? draft, int? versionId)
    {
        var (prompt, preview, http) = Clients(_ => throw new InvalidOperationException("Không được gọi API."));
        var a = await preview.PreviewAsync(5, new AiPreviewRequest { SystemPrompt = draft, PromptVersionId = versionId });
        var b = await prompt.GetAssembledPromptAsync(new AssembledPreviewRequest { ApplicationId = 5, SystemPrompt = draft, PromptVersionId = versionId });
        var c = await preview.PreviewAsync(0, new AiPreviewRequest { PromptVersionId = 3 });
        Assert.False(a.IsSuccess || b.IsSuccess || c.IsSuccess);
        Assert.Equal(PlaygroundRules.InvalidSourceMessage, a.ErrorMessage);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Quota_and_assembled_preview_use_a41_routes_and_bodies()
    {
        var (prompt, _, http) = Clients(r => r.RequestUri!.AbsolutePath.EndsWith("/quota")
            ? Json(HttpStatusCode.OK, """{"remaining":27,"limit":30,"windowMinutes":60,"resetsAt":null,"inFlight":0,"maxConcurrent":2,"aiSimulated":true}""")
            : Json(HttpStatusCode.OK, """{"applicationId":5,"userPrompt":"[CANDIDATE]","personalDataMasked":true,"maskedNames":1,"allowedSkillCodes":["csharp"]}"""));

        var quota = await prompt.GetPreviewQuotaAsync();
        var assembled = await prompt.GetAssembledPromptAsync(new AssembledPreviewRequest { ApplicationId = 5, PromptVersionId = 2 });

        Assert.Equal((27, 30, 60, true), (quota.Data!.Remaining, quota.Data.Limit, quota.Data.WindowMinutes, quota.Data.AiSimulated));
        Assert.True(assembled.Data!.PersonalDataMasked);
        Assert.Equal(("GET", "/api/applications/ai-preview/quota"), (http.Requests[0].Method, http.Requests[0].Path));
        Assert.Equal(("POST", "/api/prompts/assembled-preview"), (http.Requests[1].Method, http.Requests[1].Path));
        using var json = JsonDocument.Parse(http.Requests[1].Body!);
        Assert.Equal(["applicationId", "promptVersionId"], json.RootElement.EnumerateObject().Select(p => p.Name));
    }

    // ---------- (1) Lỗi ----------

    [Fact]
    public async Task Problem_code_and_retry_after_are_exposed_for_429()
    {
        var (_, preview, _) = Clients(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests,
                """{"title":"Hết lượt chấm thử","status":429,"detail":"Đã dùng hết 30 lượt chấm thử trong 60 phút. Thử lại sau 14:06 (giờ Việt Nam, UTC+7).","code":"PreviewQuotaExhausted","remaining":0}""",
                "application/problem+json");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(125));
            return response;
        });
        var result = await preview.PreviewAsync(5, new AiPreviewRequest { SystemPrompt = "x" });
        Assert.Equal((429, "PreviewQuotaExhausted", TimeSpan.FromSeconds(125)), (result.StatusCode, result.ErrorCode, result.RetryAfter));
        Assert.Contains("14:06", result.ErrorMessage);
    }

    [Fact]
    public async Task Masking_disabled_409_code_is_exposed_for_assembled_preview()
    {
        var (prompt, _, _) = Clients(_ => Json(HttpStatusCode.Conflict,
            """{"title":"Chưa bật che dữ liệu cá nhân","status":409,"detail":"Gemini:MaskPersonalData đang tắt…","code":"PersonalDataMaskingDisabled"}""",
            "application/problem+json"));
        var result = await prompt.GetAssembledPromptAsync(new AssembledPreviewRequest { ApplicationId = 5, SystemPrompt = "x" });
        Assert.Equal((409, PlaygroundRules.MaskingDisabledCode), (result.StatusCode, result.ErrorCode));
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task Null_json_schema_mismatch_html_error_and_network_failure_are_safe_messages()
    {
        var (_, nullBody, _) = Clients(_ => Json(HttpStatusCode.OK, "null"));
        Assert.Equal("API trả về dữ liệu rỗng.", (await nullBody.PreviewAsync(5, new AiPreviewRequest { PromptVersionId = 1 })).ErrorMessage);

        var (_, mismatch, _) = Clients(_ => Json(HttpStatusCode.OK, """{"finalScore":"không phải số"}"""));
        Assert.Equal("Dữ liệu API trả về không đúng định dạng mong đợi.", (await mismatch.PreviewAsync(5, new AiPreviewRequest { PromptVersionId = 1 })).ErrorMessage);

        var (_, html, _) = Clients(_ => Json(HttpStatusCode.InternalServerError, "<html>System.Exception: at SmartCv… ApiKey=abc</html>", "text/html"));
        var htmlResult = await html.PreviewAsync(5, new AiPreviewRequest { PromptVersionId = 1 });
        Assert.DoesNotContain("Exception", htmlResult.ErrorMessage);
        Assert.DoesNotContain("ApiKey", htmlResult.ErrorMessage);
        Assert.Null(htmlResult.ErrorCode);

        var (_, down, _) = Clients(_ => throw new HttpRequestException("connection refused 10.0.0.1"));
        var downResult = await down.PreviewAsync(5, new AiPreviewRequest { PromptVersionId = 1 });
        Assert.Null(downResult.StatusCode);
        Assert.DoesNotContain("10.0.0.1", downResult.ErrorMessage);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_being_reported_as_timeout()
    {
        var gate = new TaskCompletionSource();
        var recorder = new CancellableHandler(gate);
        var preview = new AiPreviewApiClient(new HttpClient(recorder) { BaseAddress = new Uri("https://api.test/") }, NullLogger<AiPreviewApiClient>.Instance);
        using var cts = new CancellationTokenSource();
        var task = preview.PreviewAsync(5, new AiPreviewRequest { PromptVersionId = 1 }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private sealed class CancellableHandler(TaskCompletionSource gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await gate.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // ---------- (2) Hợp đồng với API A41 thật (InMemory, AI giả) ----------

    private sealed class Api : IDisposable
    {
        public required WebApplicationFactory<Program> Factory { get; init; }
        public required PromptApiClient Prompt { get; init; }
        public required AiPreviewApiClient Preview { get; init; }
        public void Dispose() => Factory.Dispose();

        public async Task<T> DbAsync<T>(Func<SmartCvDbContext, Task<T>> action)
        {
            using var scope = Factory.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
        }
    }

    private static async Task<Api> RealApiAsync(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Gemini:UseFake"] = "true", ["Gemini:FakeDelayMs"] = "0", ["Gemini:PreviewPermitPerMinute"] = "1000",
            ["Gemini:MinDelayBetweenCallsMs"] = "0", ["Scoring:WorkerEnabled"] = "false", ["AiPreview:LimitPerWindow"] = "3"
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        var factory = TestSupport.ApiFactory("Development", settings).WithWebHostBuilder(b => b.ConfigureLogging(l => l.ClearProviders()));
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") });
        var api = new Api
        {
            Factory = factory,
            Prompt = new PromptApiClient(http, NullLogger<PromptApiClient>.Instance),
            Preview = new AiPreviewApiClient(http, NullLogger<AiPreviewApiClient>.Instance)
        };
        await api.DbAsync(db => db.Database.EnsureCreatedAsync());
        return api;
    }

    private const string CvText = "Lê Minh Châu\nEmail: chau.le@example.com · SĐT 0912 345 678\nGPA 3.2. 1.5 năm C#, ASP.NET Core, SQL Server, EF Core, Git.";

    private static async Task<(int AppId, int VersionId, int SetId)> SeedAsync(Api api) => await api.DbAsync(async db =>
    {
        var prompt = new PromptVersion { VersionName = "v2-tieu-chi", SystemPrompt = "Bạn là chuyên gia tuyển dụng.", IsActive = true, CreatedAt = DateTime.UtcNow };
        var id = Guid.NewGuid();
        db.PromptVersions.Add(prompt);
        db.Cvs.Add(new Cv
        {
            CvId = id, OriginalFileName = "cv-chau.pdf", StoredPath = $"Uploads/CV/{id}.pdf", ContentType = "application/pdf", FileSizeBytes = 1,
            FileHash = id.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = ExtractionStatuses.Completed,
            ExtractedText = CvText, UploadedAt = DateTime.UtcNow, Candidate = new Candidate { FullName = "Lê Minh Châu", CreatedAt = DateTime.UtcNow }
        });
        var application = new Application { CvId = id, JobId = 1, CreatedAt = DateTime.UtcNow };
        db.Applications.Add(application);
        var set = new EvaluationSet { Name = "Bộ B39", CreatedAt = DateTime.UtcNow };
        db.EvaluationSets.Add(set);
        await db.SaveChangesAsync();
        return (application.ApplicationId, prompt.PromptVersionId, set.EvaluationSetId);
    });

    private static Task<string> CountsAsync(Api api) => api.DbAsync(async db =>
        $"{await db.PromptVersions.CountAsync()}|{await db.PromptVersions.CountAsync(p => p.IsActive)}|{await db.EvaluationRuns.CountAsync()}|{await db.Evaluations.CountAsync()}|{await db.HrLabels.CountAsync()}");

    [Fact]
    public async Task Real_api_draft_and_version_previews_deserialize_and_never_create_or_activate_prompts()
    {
        using var api = await RealApiAsync();
        var (appId, versionId, setId) = await SeedAsync(api);
        var before = await CountsAsync(api);

        var candidates = await api.Prompt.GetLabelCandidatesAsync(setId);
        var candidate = Assert.Single(candidates.Data!);
        Assert.Null(PlaygroundRules.Unavailable(candidate));

        var quota = await api.Prompt.GetPreviewQuotaAsync();
        Assert.Equal((3, 3), (quota.Data!.Remaining, quota.Data.Limit));

        var draft = await api.Preview.PreviewAsync(appId, new AiPreviewRequest { SystemPrompt = "Bạn là chuyên gia tuyển dụng.\nThiếu kỹ năng bắt buộc thì điểm không quá 50." });
        Assert.True(draft.IsSuccess, draft.ErrorMessage);
        Assert.True(draft.Data!.IsDraft);
        Assert.Equal("Draft", draft.Data.PromptSource);
        Assert.False(draft.Data.Persisted);
        Assert.True(draft.Data.AiSimulated);
        Assert.Equal((2, 3, true), (draft.Data.RemainingTries, draft.Data.TryLimit, draft.Data.TryCounted));
        Assert.Null(draft.Data.CurrentEvaluation);
        Assert.NotEmpty(draft.Data.Skills);

        var version = await api.Preview.PreviewAsync(appId, new AiPreviewRequest { PromptVersionId = versionId });
        Assert.True(version.IsSuccess, version.ErrorMessage);
        Assert.Equal(("v2-tieu-chi", false, versionId), (version.Data!.PromptVersion, version.Data.IsDraft, version.Data.PromptVersionId));

        Assert.Equal(before, await CountsAsync(api));   // không tạo/kích hoạt prompt, không ghi lượt chấm hay nhãn
    }

    [Fact]
    public async Task Real_api_assembled_preview_is_masked_free_and_quota_429_carries_code()
    {
        using var api = await RealApiAsync(new() { ["AiPreview:LimitPerWindow"] = "1" });
        var (appId, versionId, _) = await SeedAsync(api);

        var assembled = await api.Prompt.GetAssembledPromptAsync(new AssembledPreviewRequest { ApplicationId = appId, SystemPrompt = "Quy tắc nháp B39." });
        Assert.True(assembled.IsSuccess, assembled.ErrorMessage);
        var a = assembled.Data!;
        Assert.True(a.PersonalDataMasked);
        Assert.Equal("Quy tắc nháp B39.", a.SystemPrompt);
        Assert.Contains("[EMAIL]", a.UserPrompt);
        Assert.Contains("[PHONE]", a.UserPrompt);
        Assert.DoesNotContain("chau.le@example.com", a.UserPrompt);
        Assert.DoesNotContain("0912 345 678", a.UserPrompt);
        Assert.True(a.MaskedEmails >= 1 && a.MaskedPhones >= 1);
        Assert.Equal(1, (await api.Prompt.GetPreviewQuotaAsync()).Data!.Remaining);   // xem trước không tốn lượt

        Assert.True((await api.Preview.PreviewAsync(appId, new AiPreviewRequest { PromptVersionId = versionId })).IsSuccess);
        var exhausted = await api.Preview.PreviewAsync(appId, new AiPreviewRequest { PromptVersionId = versionId });
        Assert.Equal((429, "PreviewQuotaExhausted"), (exhausted.StatusCode, exhausted.ErrorCode));
        Assert.NotNull(exhausted.RetryAfter);
        Assert.True(PlaygroundRules.DescribePreviewError(exhausted.StatusCode, exhausted.ErrorCode, exhausted.ErrorMessage).QuotaExhausted);
        Assert.Equal(0, (await api.Prompt.GetPreviewQuotaAsync()).Data!.Remaining);
    }

    [Fact]
    public async Task Real_api_masking_disabled_returns_409_code_without_payload()
    {
        using var api = await RealApiAsync(new() { ["Gemini:MaskPersonalData"] = "false" });
        var (appId, _, _) = await SeedAsync(api);
        var result = await api.Prompt.GetAssembledPromptAsync(new AssembledPreviewRequest { ApplicationId = appId, SystemPrompt = "x" });
        Assert.Equal((409, PlaygroundRules.MaskingDisabledCode), (result.StatusCode, result.ErrorCode));
        Assert.DoesNotContain("chau.le", result.ErrorMessage);
    }
}
