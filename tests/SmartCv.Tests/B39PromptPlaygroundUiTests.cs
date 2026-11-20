extern alias web;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using SmartCv.Core.Privacy;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Components.Prompts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B39: trang Prompt thật + khung "Thử với hồ sơ…" thật, HTTP giả theo hợp đồng A41 (không Gemini, không database).
/// Kiểm tra: nháp/phiên bản, không tạo/kích hoạt prompt, giữ bản nháp, kết quả cũ, request chồng/muộn, lượt thử, che dữ liệu.
/// </summary>
#pragma warning disable BL0006 // RenderTree APIs are used only for component interaction tests.
public class B39PromptPlaygroundUiTests
{
    private const string SourceContent = "Bạn là chuyên gia tuyển dụng.\nChỉ dựa trên bằng chứng trong CV.";
    private const string AddedRule = "Thiếu kỹ năng bắt buộc thì điểm không quá 50.";
    private const string DraftContent = SourceContent + "\n" + AddedRule;
    private const string TryButton = "Thử với hồ sơ…";
    private const string RunVersion = "Chấm thử v2-tieu-chi";
    private const string RunDraft = "Chấm thử bản nháp";
    private const string Editor = "Nội dung system prompt";

    // ---------------- HTTP giả theo hợp đồng A41 ----------------

    private sealed class A41Handler : HttpMessageHandler
    {
        public List<PromptVersionDto> Versions { get; } =
        [
            new() { PromptVersionId = 2, VersionName = "v2-tieu-chi", SystemPrompt = SourceContent, IsActive = true, HasRuns = true,
                LatestAccuracyPercent = 80, CreatedAt = new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc) },
            new() { PromptVersionId = 1, VersionName = "v1-co-ban", SystemPrompt = "Vai trò cũ.\nQuy tắc cũ.", HasRuns = true,
                LatestAccuracyPercent = 70, CreatedAt = new DateTime(2026, 11, 9, 2, 0, 0, DateTimeKind.Utc) }
        ];
        public List<EvaluationSetDto> Sets { get; set; } =
        [
            new() { EvaluationSetId = 7, Name = "Bộ B39 chính", LabelCount = 3, PassCount = 1 },
            new() { EvaluationSetId = 8, Name = "Bộ dự phòng", LabelCount = 2, PassCount = 1 }
        ];
        public List<LabelCandidateDto> Candidates { get; } =
        [
            Candidate(11, "Nguyễn Văn An", "cv-an.pdf", label: true),
            Candidate(12, "Trần Thị Bình", "cv-binh.pdf", label: false),
            Candidate(13, "Lê Văn Cường", "cv-cuong.pdf", status: "Failed"),
            Candidate(14, "Phạm Thị Dung", "cv-dung.pdf", status: "Pending"),
            Candidate(15, null, RetentionRules.AnonymizedFileName, text: false),
            Candidate(21, "Hoàng Minh Em", "cv-em.pdf", jobId: 2, job: "Tester")
        ];
        public PreviewQuotaDto Quota { get; set; } = new() { Remaining = 28, Limit = 30, WindowMinutes = 60, MaxConcurrent = 2 };
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<JsonElement> PreviewBodies { get; } = new();
        public ConcurrentQueue<JsonElement> AssembledBodies { get; } = new();
        public ConcurrentQueue<int> CandidateSetIds { get; } = new();
        public TaskCompletionSource? PreviewGate { get; set; }
        public Func<int, JsonElement, HttpResponseMessage?>? PreviewOverride { get; set; }
        public Func<JsonElement, HttpResponseMessage?>? AssembledOverride { get; set; }
        public Dictionary<int, TaskCompletionSource> CandidateGates { get; } = [];
        public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            Calls.Enqueue($"{request.Method} {path}");
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (request.Method == HttpMethod.Get && path == "/api/prompts") return Json(HttpStatusCode.OK, Versions.ToList());
            if (request.Method == HttpMethod.Get && path == "/api/evaluation-sets") return Json(HttpStatusCode.OK, Sets.ToList());
            if (request.Method == HttpMethod.Get && parts is ["api", "evaluation-sets", var set, "applications"])
            {
                var id = int.Parse(set);
                CandidateSetIds.Enqueue(id);
                if (CandidateGates.TryGetValue(id, out var gate)) await gate.Task.WaitAsync(ct);
                return Json(HttpStatusCode.OK, Candidates.Select(c => WithLabel(c, id)).ToList());
            }
            if (request.Method == HttpMethod.Get && path == "/api/applications/ai-preview/quota") return Json(HttpStatusCode.OK, Quota);
            if (request.Method == HttpMethod.Post && parts is ["api", "applications", var app, "ai-preview"])
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                PreviewBodies.Enqueue(body);
                if (PreviewGate is not null) await PreviewGate.Task.WaitAsync(ct);
                return PreviewOverride?.Invoke(int.Parse(app), body) ?? Preview(int.Parse(app), body);
            }
            if (request.Method == HttpMethod.Post && path == "/api/prompts/assembled-preview")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                AssembledBodies.Enqueue(body);
                return AssembledOverride?.Invoke(body) ?? Assembled(body);
            }
            // Các API khác của trang (prompt-tests, so sánh...) không thuộc B39.
            return Json(HttpStatusCode.NotFound, new { detail = "Route không thuộc B39 test." });
        }

        // Nhãn "· HR" chỉ có trong bộ 7 để kiểm tra danh sách theo đúng bộ.
        private static LabelCandidateDto WithLabel(LabelCandidateDto c, int setId) => new()
        {
            ApplicationId = c.ApplicationId, CvId = c.CvId, HasExtractedText = c.HasExtractedText, CandidateName = c.CandidateName, FileName = c.FileName,
            JobId = c.JobId, JobTitle = c.JobTitle, ExtractionStatus = c.ExtractionStatus, CurrentLabel = setId == 7 ? c.CurrentLabel : null
        };

        private HttpResponseMessage Preview(int app, JsonElement body)
        {
            if (Quota.Remaining <= 0)
                return Json(HttpStatusCode.TooManyRequests, new { title = "Hết lượt chấm thử", detail = "Đã dùng hết 30 lượt chấm thử trong 60 phút. Thử lại sau 14:06 (giờ Việt Nam, UTC+7).", code = "PreviewQuotaExhausted" });
            Quota = new() { Remaining = Quota.Remaining - 1, Limit = Quota.Limit, WindowMinutes = Quota.WindowMinutes, MaxConcurrent = 2 };
            var draft = body.TryGetProperty("systemPrompt", out var p) ? p.GetString() : null;
            var versionId = body.TryGetProperty("promptVersionId", out var v) ? v.GetInt32() : (int?)null;
            var final = draft?.Contains(AddedRule) == true ? 50m : 78.5m;
            var dto = new AiPreviewDto(draft is null ? Versions.Single(x => x.PromptVersionId == versionId).VersionName : "Nháp (chưa lưu)",
                "fake-ai (giả lập)", 62m, final == 50m ? 45m : 85m, final, "Combined", final == 50m ? "Chưa phù hợp" : "Phù hợp", final >= 70,
                1.5m, $"Nhận xét cho hồ sơ {app}.", true, "None", null, 1, true, false, ["csharp"], ["docker"], [],
                [new("csharp", "C#", "MustHave", true, "Keyword", null), new("docker", "Docker", "MustHave", false, null, null),
                 new("git", "Git", "NiceToHave", true, "AI", null)])
            {
                KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70, PromptSource = draft is null ? "Version" : "Draft",
                PromptVersionId = versionId, IsDraft = draft is not null, RemainingTries = Quota.Remaining, TryLimit = Quota.Limit,
                TryCounted = true, CurrentEvaluation = app == 11 ? new CurrentEvaluationDto
                {
                    EvaluationId = 90, RunId = 4, RunType = "Screening", PromptVersionId = 2, PromptVersionName = "v2-tieu-chi",
                    ModelName = "gemini-test", KeywordScore = 62, AiScore = 76, FinalScore = 72, ScoreSource = "Combined",
                    FitLabel = "Phù hợp", PredictedPass = true, MatchedSkills = ["csharp"], MissingSkills = ["docker", "git"],
                    CreatedAt = new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc)
                } : null
            };
            return Json(HttpStatusCode.OK, dto);
        }

        private static HttpResponseMessage Assembled(JsonElement body)
        {
            var draft = body.TryGetProperty("systemPrompt", out var p) ? p.GetString() : null;
            return Json(HttpStatusCode.OK, new AssembledPromptDto
            {
                ApplicationId = body.GetProperty("applicationId").GetInt32(), JobId = 1, JobTitle = "Backend Developer (.NET)",
                PromptSource = draft is null ? "Version" : "Draft", PromptVersion = draft is null ? "v2-tieu-chi" : "Nháp (chưa lưu)", IsDraft = draft is not null,
                Model = "gemini-test", Temperature = 0.2m, AiSimulated = true, SystemPrompt = (draft ?? SourceContent).Trim(),
                UserPrompt = "Vị trí: Backend Developer (.NET)\nCV:\nỨng viên: [CANDIDATE]\nEmail: [EMAIL]\nĐiện thoại: [PHONE]\nC#, ASP.NET Core",
                PersonalDataMasked = true, MaskedNames = 1, MaskedEmails = 1, MaskedPhones = 1, MaskedTotal = 3, NameMaskSource = "Provided",
                CvCharacters = 1234, CvCharactersSent = 1234, MaxCvCharacters = 8000, AllowedSkillCodes = ["aspnetcore", "csharp"]
            });
        }

        public static HttpResponseMessage Json<T>(HttpStatusCode status, T body) => new(status) { Content = JsonContent.Create(body) };
    }

    private static LabelCandidateDto Candidate(int id, string? name, string file, string status = "Completed", bool text = true,
        bool? label = null, int jobId = 1, string job = "Backend Developer (.NET)") => new()
    {
        ApplicationId = id, CvId = Guid.NewGuid(), HasExtractedText = text, CandidateName = name, FileName = file,
        JobId = jobId, JobTitle = job, ExtractionStatus = status, CurrentLabel = label
    };

    // ---------------- Renderer ----------------

    private sealed class PromptNavigationManager : NavigationManager
    {
        public PromptNavigationManager() => Initialize("https://app.test/", "https://app.test/prompts");
        protected override void SetNavigationLockState(bool value) { }
        protected override void HandleLocationChangingHandlerException(Exception exception, LocationChangingContext context) =>
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override void NavigateToCore(string uri, NavigationOptions options) { Uri = ToAbsoluteUri(uri).ToString(); NotifyLocationChanged(false); }
    }

    private sealed class NoopJsRuntime : IJSRuntime, IJSObjectReference
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Enqueue(identifier);
            return ValueTask.FromResult(typeof(TValue) == typeof(IJSObjectReference) ? (TValue)(object)this : default!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestRenderer : Renderer
    {
        public TestRenderer(IServiceProvider services) : base(services, NullLoggerFactory.Instance) =>
            ElementReferenceContext = new WebElementReferenceContext(services.GetRequiredService<IJSRuntime>());
        public List<Exception> Errors { get; } = [];
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", isInteractive: true);
        protected override void HandleException(Exception exception) => Errors.Add(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        private int rootId;

        public Task StartAsync(Type type, ParameterView? parameters = null) => Dispatcher.InvokeAsync(async () =>
        {
            rootId = AssignRootComponentId(InstantiateComponent(type));
            await RenderRootComponentAsync(rootId, parameters ?? ParameterView.Empty);
        });

        public Task SetParametersAsync(ParameterView parameters) => Dispatcher.InvokeAsync(() => RenderRootComponentAsync(rootId, parameters));

        public Task RemoveRootAsync() => Dispatcher.InvokeAsync(() => RemoveRootComponent(rootId));

        private List<RenderTreeFrame[]> Components(int id)
        {
            var range = GetCurrentRenderTreeFrames(id);
            var frames = range.Array.Take(range.Count).ToArray();
            var all = new List<RenderTreeFrame[]> { frames };
            foreach (var f in frames.Where(f => f.FrameType == RenderTreeFrameType.Component)) all.AddRange(Components(f.ComponentId));
            return all;
        }

        private static string Clean(IEnumerable<RenderTreeFrame> frames) =>
            System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(string.Concat(frames.Select(f => f.FrameType switch
            {
                RenderTreeFrameType.Text => f.TextContent,
                RenderTreeFrameType.Markup => System.Text.RegularExpressions.Regex.Replace(f.MarkupContent, "<[^>]+>", " "),
                _ => " "
            }))), @"\s+", " ").Trim();

        public string Text => Dispatcher.InvokeAsync(() => string.Join(" ", Components(rootId).Select(Clean))).Result;

        public bool HasMarkup(string markup) => Dispatcher.InvokeAsync(() => Components(rootId)
            .Any(frames => frames.Any(f => f.FrameType == RenderTreeFrameType.Markup && f.MarkupContent.Contains(markup, StringComparison.Ordinal)))).Result;

        private static string? Attr(RenderTreeFrame[] frames, int element, string name)
        {
            for (var j = element + 1; j < frames.Length && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
                if (frames[j].AttributeName == name) return frames[j].AttributeValue?.ToString();
            return null;
        }

        private static string Inner(RenderTreeFrame[] frames, int element) => Clean(frames.Skip(element + 1).Take(frames[element].ElementSubtreeLength - 1));

        private (RenderTreeFrame[] Frames, int Index)? TryFind(Func<RenderTreeFrame[], int, bool> match)
        {
            foreach (var frames in Components(rootId))
                for (var i = 0; i < frames.Length; i++)
                    if (frames[i].FrameType == RenderTreeFrameType.Element && match(frames, i)) return (frames, i);
            return null;
        }

        private (RenderTreeFrame[] Frames, int Index) Find(Func<RenderTreeFrame[], int, bool> match) =>
            TryFind(match) ?? throw new Xunit.Sdk.XunitException("Không tìm thấy phần tử trên trang Prompt.");

        private ulong Handler((RenderTreeFrame[] Frames, int Index) found, string eventName)
        {
            var (frames, i) = found;
            for (var j = i + 1; j < frames.Length && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
                if (frames[j].AttributeName == eventName) return frames[j].AttributeEventHandlerId;
            throw new Xunit.Sdk.XunitException($"Không tìm thấy sự kiện {eventName}.");
        }

        private static bool Button(RenderTreeFrame[] f, int i, string text) => f[i].ElementName == "button" && Inner(f, i).Contains(text, StringComparison.Ordinal);

        public Task ClickAsync(string text) => Dispatcher.InvokeAsync(() =>
            DispatchEventAsync(Handler(Find((f, i) => Button(f, i, text)), "onclick"), null, new MouseEventArgs()));

        public Task ClickAttrAsync(string attribute, string value) => Dispatcher.InvokeAsync(() =>
            DispatchEventAsync(Handler(Find((f, i) => f[i].ElementName == "button" && Attr(f, i, attribute) == value), "onclick"), null, new MouseEventArgs()));

        public Task InputAsync(string label, string content) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Handler(Find((f, i) => (f[i].ElementName is "input" or "textarea") && Attr(f, i, "aria-label") == label), "oninput"),
            null, new ChangeEventArgs { Value = content }));

        public Task ChangeAsync(string id, object value) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Handler(Find((f, i) => f[i].ElementName == "select" && Attr(f, i, "id") == id), "onchange"), null, new ChangeEventArgs { Value = value.ToString() }));

        public string Value(string label) => Dispatcher.InvokeAsync(() =>
        {
            var (frames, i) = Find((f, n) => (f[n].ElementName is "input" or "textarea") && Attr(f, n, "aria-label") == label);
            return Attr(frames, i, "value") ?? "";
        }).Result;

        public bool Exists(string text) => Dispatcher.InvokeAsync(() => TryFind((f, i) => Button(f, i, text)) is not null).Result;

        public bool Disabled(string buttonText) => Dispatcher.InvokeAsync(() =>
        {
            var (frames, i) = Find((f, n) => Button(f, n, buttonText));
            return Attr(frames, i, "disabled") is not null;
        }).Result;

        public bool OptionDisabled(string value) => Dispatcher.InvokeAsync(() =>
        {
            var (frames, i) = Find((f, n) => f[n].ElementName == "option" && Attr(f, n, "value") == value);
            return Attr(frames, i, "disabled") is not null;
        }).Result;

        public string OptionText(string value) => Dispatcher.InvokeAsync(() =>
        {
            var (frames, i) = Find((f, n) => f[n].ElementName == "option" && Attr(f, n, "value") == value);
            return Inner(frames, i);
        }).Result;

        public string? SelectValue(string id) => Dispatcher.InvokeAsync(() =>
        {
            var (frames, i) = Find((f, n) => f[n].ElementName == "select" && Attr(f, n, "id") == id);
            return Attr(frames, i, "value");
        }).Result;
    }

    private sealed class Session(TestRenderer renderer, ServiceProvider services, A41Handler api, NoopJsRuntime js) : IAsyncDisposable
    {
        public TestRenderer R { get; } = renderer;
        public A41Handler Api { get; } = api;
        public NoopJsRuntime Js { get; } = js;
        public async ValueTask DisposeAsync() { await R.DisposeAsync(); await services.DisposeAsync(); }
    }

    private static async Task<Session> OpenPageAsync(A41Handler? api = null, bool anonymous = false)
    {
        api ??= new A41Handler();
        var (renderer, services, js) = await RendererAsync(api, anonymous);
        await renderer.StartAsync(typeof(Prompts));
        await WaitUntil(() => renderer.Value(Editor) == SourceContent);
        return new Session(renderer, services, api, js);
    }

    private static async Task<(TestRenderer, ServiceProvider, NoopJsRuntime)> RendererAsync(A41Handler api, bool anonymous)
    {
        var js = new NoopJsRuntime();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new PromptNavigationManager());
        services.AddSingleton<IJSRuntime>(js);
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("https://api.test/") };
        services.AddSingleton(new PromptApiClient(http, NullLogger<PromptApiClient>.Instance));
        services.AddSingleton(new AiPreviewApiClient(http, NullLogger<AiPreviewApiClient>.Instance));
        var anonymity = new AnonymityState();
        if (!anonymous) await anonymity.SetAsync(false);   // tên hiển thị như khi HR tắt Ẩn danh
        services.AddSingleton(anonymity);
        services.AddB28Auth(); // B28: Admin thấy Lưu / Nội dung gửi Gemini như trước
        var provider = services.BuildServiceProvider();
        return (new TestRenderer(provider), provider, js);
    }

    private static async Task OpenPlaygroundAsync(Session s)
    {
        await s.R.ClickAsync(TryButton);
        await WaitUntil(() => s.R.Text.Contains("Còn 28/30 lượt thử trong 60 phút") && s.R.Text.Contains("/5 sẵn sàng"));
    }

    private static async Task ChooseAsync(Session s, int jobId, int applicationId)
    {
        await s.R.ChangeAsync("pg-job", jobId);
        await s.R.ChangeAsync("pg-app", applicationId);
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 5)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void NoPromptMutations(A41Handler api)
    {
        Assert.Equal(0, api.Count("POST /api/prompts/") - api.Count("POST /api/prompts/assembled-preview"));
        Assert.DoesNotContain(api.Calls, c => c == "POST /api/prompts" || c.EndsWith("/activate", StringComparison.Ordinal));
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("PUT ", StringComparison.Ordinal) || c.StartsWith("PATCH ", StringComparison.Ordinal) || c.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.Equal(0, api.Count("POST /api/prompt-tests"));
    }

    // =====================================================================================
    // 1. Mở khung, nguồn phiên bản / bản nháp, hai cột so sánh
    // =====================================================================================

    [Fact]
    public async Task Try_button_sits_next_to_undo_and_opens_playground_with_quota_and_page_selected_set()
    {
        await using var s = await OpenPageAsync();
        Assert.True(s.R.Exists(TryButton));
        Assert.Contains("Thử với hồ sơ… Hoàn tác Lưu thành phiên bản mới", s.R.Text);
        Assert.DoesNotContain("Chấm thử v2-tieu-chi", s.R.Text);

        await OpenPlaygroundAsync(s);

        Assert.Contains("Còn 28/30 lượt thử trong 60 phút", s.R.Text);
        Assert.Contains("Phiên bản đã lưu", s.R.Text);
        Assert.True(s.R.Disabled(RunVersion));   // chưa chọn hồ sơ
        Assert.Contains("Chọn công việc và hồ sơ để chấm thử.", s.R.Text);
        Assert.Equal("7", s.R.SelectValue("pg-set"));   // bộ đang chọn của trang (So sánh tỷ lệ đúng chọn bộ đầu tiên)
        Assert.All(s.Api.CandidateSetIds, id => Assert.Equal(7, id));
        Assert.Contains(s.Js.Calls, c => c.Contains("focus", StringComparison.OrdinalIgnoreCase));   // tiêu điểm chuyển vào khung
        NoPromptMutations(s.Api);
    }

    [Fact]
    public async Task Unchanged_prompt_runs_by_version_id_and_shows_both_columns_with_delta()
    {
        await using var s = await OpenPageAsync();
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        Assert.False(s.R.Disabled(RunVersion));

        await s.R.ClickAsync(RunVersion);

        var body = Assert.Single(s.Api.PreviewBodies);
        Assert.Equal(["promptVersionId"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, body.GetProperty("promptVersionId").GetInt32());
        var text = s.R.Text;
        Assert.Contains("Chấm thử · không lưu", text);
        Assert.Contains("Đã lưu · v2-tieu-chi", text);
        Assert.Contains("78,5", text);
        Assert.Contains("+6,5 điểm so với bản đã lưu", text);
        Assert.Contains("Lượt #4", text);
        Assert.Contains("Còn 27/30 lượt thử trong 60 phút", text);   // số lượt lấy từ phản hồi API
        Assert.Contains("AI giả lập", text);
        Assert.Contains("Thiếu Docker (bắt buộc)", text);
        Assert.Contains("+ đạt thêm: Git", text);
        NoPromptMutations(s.Api);
    }

    [Fact]
    public async Task Edited_prompt_runs_as_draft_keeps_editor_state_and_never_saves_a_version()
    {
        await using var s = await OpenPageAsync();
        await OpenPlaygroundAsync(s);
        await s.R.InputAsync(Editor, DraftContent);
        await s.R.InputAsync("Tên phiên bản mới", "v3-tran-50");
        await s.R.InputAsync("Ghi chú thay đổi", "Giới hạn điểm khi thiếu kỹ năng.");
        Assert.True(s.R.Exists(RunDraft));
        Assert.Contains("Bản nháp · chưa lưu", s.R.Text);

        await ChooseAsync(s, 1, 11);
        await s.R.ClickAsync(RunDraft);

        var body = Assert.Single(s.Api.PreviewBodies);
        Assert.Equal(["systemPrompt"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(DraftContent, body.GetProperty("systemPrompt").GetString());
        Assert.Contains("−22 điểm so với bản đã lưu", s.R.Text);
        Assert.Contains("Đã lưu · v2-tieu-chi", s.R.Text);           // cột đã lưu giữ nguyên
        Assert.Equal(DraftContent, s.R.Value(Editor));                 // không mất chữ đang gõ
        Assert.Equal("v3-tran-50", s.R.Value("Tên phiên bản mới"));
        Assert.Equal("Giới hạn điểm khi thiếu kỹ năng.", s.R.Value("Ghi chú thay đổi"));
        Assert.Equal(2, s.Api.Versions.Count);
        NoPromptMutations(s.Api);
    }

    [Fact]
    public async Task Results_of_previous_content_are_labelled_after_editing_and_cleared_when_application_changes()
    {
        await using var s = await OpenPageAsync();
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        await s.R.ClickAsync(RunVersion);
        Assert.DoesNotContain("Kết quả của nội dung trước", s.R.Text);

        await s.R.InputAsync(Editor, DraftContent);
        Assert.Contains("Kết quả của nội dung trước", s.R.Text);
        Assert.Contains("(nội dung trước)", s.R.Text);
        Assert.Contains("chưa cập nhật", s.R.Text);

        await s.R.InputAsync(Editor, SourceContent + "\n");   // quay về đúng nội dung đã chấm (chỉ khác xuống dòng cuối)
        Assert.DoesNotContain("Kết quả của nội dung trước", s.R.Text);

        await s.R.ChangeAsync("pg-app", 12);
        Assert.DoesNotContain("78,5", s.R.Text);
        Assert.Contains("Chưa chấm thử", s.R.Text);
    }

    [Fact]
    public async Task Application_without_saved_evaluation_shows_message_and_no_delta()
    {
        await using var s = await OpenPageAsync();
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 12);
        await s.R.ClickAsync(RunVersion);
        Assert.Contains("Hồ sơ chưa có kết quả đã lưu để so", s.R.Text);
        Assert.DoesNotContain("điểm so với bản đã lưu", s.R.Text);
    }

    [Fact]
    public async Task Changing_job_resets_application_and_results()
    {
        await using var s = await OpenPageAsync();
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        await s.R.ClickAsync(RunVersion);
        await s.R.ChangeAsync("pg-job", 2);
        Assert.Equal("0", s.R.SelectValue("pg-app"));
        Assert.DoesNotContain("Đã lưu · v2-tieu-chi", s.R.Text);
        Assert.True(s.R.Disabled(RunVersion));
        Assert.Contains("Hoàng Minh Em · HS21", s.R.Text);
    }

    [Fact]
    public async Task Selecting_another_version_in_history_clears_trial_results()
    {
        await using var s = await OpenPageAsync();
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        await s.R.ClickAsync(RunVersion);
        await s.R.ClickAsync("v1-co-ban");
        await WaitUntil(() => s.R.Exists("Chấm thử v1-co-ban"));
        Assert.DoesNotContain("78,5", s.R.Text);
        Assert.Equal("11", s.R.SelectValue("pg-app"));   // lựa chọn hồ sơ không nhạy cảm được giữ
    }

    // =====================================================================================
    // 2. Hồ sơ đủ điều kiện, ẩn danh
    // =====================================================================================

    [Fact]
    public async Task Unready_and_anonymized_applications_are_disabled_with_reasons_and_no_identity()
    {
        await using var s = await OpenPageAsync();
        await OpenPlaygroundAsync(s);
        await s.R.ChangeAsync("pg-job", 1);
        Assert.False(s.R.OptionDisabled("11"));
        Assert.Equal("Nguyễn Văn An · HS11 · HR: Đạt", s.R.OptionText("11"));
        Assert.Equal("Trần Thị Bình · HS12 · HR: Không đạt", s.R.OptionText("12"));
        Assert.True(s.R.OptionDisabled("13"));
        Assert.Contains("đọc CV lỗi", s.R.OptionText("13"));
        Assert.True(s.R.OptionDisabled("14"));
        Assert.Contains("CV đang được đọc", s.R.OptionText("14"));
        Assert.True(s.R.OptionDisabled("15"));
        Assert.Equal("Hồ sơ đã ẩn danh · HS15 — đã ẩn danh hóa theo hạn lưu", s.R.OptionText("15"));
        Assert.Contains("Backend Developer (.NET) · 2/5 sẵn sàng", s.R.Text);
    }

    [Fact]
    public async Task Anonymous_mode_hides_candidate_names_in_the_picker_and_free_text()
    {
        await using var s = await OpenPageAsync(anonymous: true);
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        Assert.Equal("Ứng viên #11 · HS11 · HR: Đạt", s.R.OptionText("11"));
        Assert.DoesNotContain("Nguyễn Văn An", s.R.OptionText("11"));
        await s.R.ClickAsync(RunVersion);
        Assert.DoesNotContain("Nhận xét cho hồ sơ 11.", s.R.Text);
        Assert.Contains("Nội dung tự do được ẩn", s.R.Text);
    }

    [Fact]
    public async Task Application_becoming_unready_between_pick_and_run_reports_409_and_reloads_list()
    {
        var api = new A41Handler
        {
            PreviewOverride = (_, _) => A41Handler.Json(HttpStatusCode.Conflict, new { title = "CV đã được ẩn danh hóa", detail = "Nội dung CV đã được xóa theo chính sách lưu trữ." })
        };
        await using var s = await OpenPageAsync(api);
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        var loadsBefore = api.CandidateSetIds.Count;
        api.Candidates[0] = Candidate(11, null, RetentionRules.AnonymizedFileName, text: false);
        await s.R.ClickAsync(RunVersion);
        Assert.Contains("Hồ sơ chưa thể chấm thử", s.R.Text);
        Assert.Contains("Nội dung CV đã được xóa", s.R.Text);
        await WaitUntil(() => api.CandidateSetIds.Count > loadsBefore);
        await WaitUntil(() => s.R.Text.Contains("đã ẩn danh hóa theo hạn lưu."));
        Assert.True(s.R.Disabled(RunVersion));
        Assert.Single(api.PreviewBodies);
    }

    // =====================================================================================
    // 3. Lượt thử, lỗi API, request chồng / muộn
    // =====================================================================================

    [Fact]
    public async Task Quota_429_locks_the_button_refreshes_quota_from_api_and_does_not_retry()
    {
        var api = new A41Handler { Quota = new() { Remaining = 1, Limit = 30, WindowMinutes = 60, ResetsAt = DateTime.UtcNow.AddMinutes(10) } };
        await using var s = await OpenPageAsync(api);
        await s.R.ClickAsync(TryButton);
        await WaitUntil(() => s.R.Text.Contains("Còn 1/30 lượt thử trong 60 phút") && s.R.Text.Contains("/5 sẵn sàng"));
        await ChooseAsync(s, 1, 11);
        await s.R.ClickAsync(RunVersion);
        Assert.Contains("Đã hết 30 lượt thử trong 60 phút", s.R.Text);   // phản hồi cuối: còn 0
        Assert.True(s.R.Disabled(RunVersion));
        Assert.Contains("Đã hết lượt thử.", s.R.Text);

        // "Kiểm tra lại lượt" chỉ đọc quota (không tốn lượt); máy chủ vẫn hết lượt → nút vẫn khóa.
        api.Quota = new() { Remaining = 0, Limit = 30, WindowMinutes = 60 };
        var quotaReads = api.Count("GET /api/applications/ai-preview/quota");
        await s.R.ClickAsync("Kiểm tra lại lượt");
        await WaitUntil(() => api.Count("GET /api/applications/ai-preview/quota") > quotaReads);
        Assert.True(s.R.Disabled(RunVersion));
        Assert.Single(api.PreviewBodies);
    }

    [Fact]
    public async Task Server_side_429_shows_vietnamese_message_and_reads_quota_again()
    {
        var api = new A41Handler
        {
            PreviewOverride = (_, _) => A41Handler.Json(HttpStatusCode.TooManyRequests, new
            {
                title = "Hết lượt chấm thử", detail = "Đã dùng hết 30 lượt chấm thử trong 60 phút. Thử lại sau 14:06 (giờ Việt Nam, UTC+7).", code = "PreviewQuotaExhausted"
            })
        };
        await using var s = await OpenPageAsync(api);
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        var quotaReads = api.Count("GET /api/applications/ai-preview/quota");
        api.Quota = new() { Remaining = 0, Limit = 30, WindowMinutes = 60 };
        await s.R.ClickAsync(RunVersion);
        Assert.Contains("Hết lượt chấm thử", s.R.Text);
        Assert.Contains("Thử lại sau 14:06", s.R.Text);
        Assert.True(api.Count("GET /api/applications/ai-preview/quota") > quotaReads);
        Assert.Contains("Đã hết 30 lượt thử trong 60 phút", s.R.Text);
        Assert.False(s.R.Exists("Thử lại"));   // không có nút thử lại khi hết lượt
        Assert.Single(api.PreviewBodies);
    }

    [Fact]
    public async Task Busy_or_unavailable_server_allows_manual_retry_only()
    {
        var responses = new Queue<HttpResponseMessage>([
            A41Handler.Json(HttpStatusCode.ServiceUnavailable, new { title = "Chưa đọc được dữ liệu CV", detail = "Dữ liệu đang được bảo vệ hoặc kết nối chưa sẵn sàng." })]);
        var api = new A41Handler();
        api.PreviewOverride = (_, _) => responses.Count > 0 ? responses.Dequeue() : null;
        await using var s = await OpenPageAsync(api);
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        await s.R.ClickAsync(RunVersion);
        Assert.Contains("Máy chủ chưa phản hồi", s.R.Text);
        Assert.Contains("có thể đã được tính", s.R.Text);
        await Task.Delay(200);
        Assert.Single(api.PreviewBodies);   // không tự thử lại
        await s.R.ClickAsync("Thử lại");
        Assert.Equal(2, api.PreviewBodies.Count);
        Assert.Contains("+6,5 điểm so với bản đã lưu", s.R.Text);
    }

    [Fact]
    public async Task Double_click_sends_one_request_and_edit_during_request_marks_result_stale()
    {
        var api = new A41Handler { PreviewGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var s = await OpenPageAsync(api);
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        var first = s.R.ClickAsync(RunVersion);
        await WaitUntil(() => api.PreviewBodies.Count == 1);
        Assert.Contains("Đang chấm thử…", s.R.Text);
        Assert.True(s.R.Disabled("Đang chấm thử…"));
        Assert.True(s.R.Exists("Dừng chờ"));
        await s.R.ClickAsync("Đang chấm thử…");      // bấm lần hai trong lúc chờ: bị bỏ qua
        await s.R.ClickAsync("Đang chấm thử…");

        await s.R.InputAsync(Editor, DraftContent);   // vẫn soạn được trong lúc chờ
        api.PreviewGate.SetResult();
        await first;

        Assert.Single(api.PreviewBodies);
        Assert.Equal(2, api.PreviewBodies.Single().GetProperty("promptVersionId").GetInt32());
        Assert.Contains("Kết quả của nội dung trước", s.R.Text);
        Assert.Equal(DraftContent, s.R.Value(Editor));
        Assert.Empty(s.R.Errors);
    }

    [Fact]
    public async Task Stop_waiting_cancels_request_and_rereads_quota()
    {
        var api = new A41Handler { PreviewGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var s = await OpenPageAsync(api);
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        var run = s.R.ClickAsync(RunVersion);
        await WaitUntil(() => api.PreviewBodies.Count == 1);
        var quotaReads = api.Count("GET /api/applications/ai-preview/quota");
        await s.R.ClickAsync("Dừng chờ");
        await run;
        await WaitUntil(() => api.Count("GET /api/applications/ai-preview/quota") > quotaReads);
        Assert.Contains("Đã dừng chờ kết quả", s.R.Text);
        Assert.False(s.R.Disabled(RunVersion));
        Assert.Empty(s.R.Errors);
    }

    [Fact]
    public async Task Closing_while_running_cancels_without_errors_and_reopening_keeps_selection_not_results()
    {
        var api = new A41Handler { PreviewGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var s = await OpenPageAsync(api);
        await OpenPlaygroundAsync(s);
        await s.R.InputAsync(Editor, DraftContent);
        await ChooseAsync(s, 1, 11);
        var run = s.R.ClickAsync(RunDraft);
        await WaitUntil(() => api.PreviewBodies.Count == 1);

        await s.R.ClickAttrAsync("aria-label", "Đóng khung thử với hồ sơ");
        await run;
        Assert.DoesNotContain("Còn 28/30", s.R.Text);
        Assert.Equal(DraftContent, s.R.Value(Editor));
        api.PreviewGate.SetResult();

        await s.R.ClickAsync(TryButton);
        await WaitUntil(() => s.R.SelectValue("pg-app") == "11");
        Assert.Equal("1", s.R.SelectValue("pg-job"));
        Assert.DoesNotContain("Chấm thử · không lưu", s.R.Text);
        Assert.Contains("Chưa chấm thử", s.R.Text);
        Assert.Empty(s.R.Errors);
        NoPromptMutations(api);
    }

    // =====================================================================================
    // 4. Nội dung gửi Gemini (đã che)
    // =====================================================================================

    [Fact]
    public async Task Payload_tab_shows_server_masked_content_without_spending_quota_and_refreshes_after_edit()
    {
        await using var s = await OpenPageAsync();
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        await s.R.ClickAttrAsync("aria-controls", "pg-panel-payload");
        await WaitUntil(() => s.R.Text.Contains("Đã che thông tin cá nhân"));

        var text = s.R.Text;
        Assert.Contains("đã che 1 họ tên, 1 email, 1 số điện thoại", text);
        Assert.Contains("[CANDIDATE]", text);
        Assert.Contains("[EMAIL]", text);
        Assert.Contains("[PHONE]", text);
        Assert.Contains("CV 1.234 ký tự", text);
        Assert.Contains("Model gemini-test", text);
        Assert.Equal(["applicationId", "promptVersionId"], s.Api.AssembledBodies.Single().EnumerateObject().Select(p => p.Name));
        Assert.Empty(s.Api.PreviewBodies);
        Assert.Contains("Còn 28/30 lượt thử", text);

        // Không gọi lại khi render lại mà không đổi gì
        await s.R.ClickAttrAsync("aria-controls", "pg-panel-payload");
        await Task.Delay(700);
        Assert.Single(s.Api.AssembledBodies);

        // Sửa prompt khi đang xem → chờ ngừng gõ rồi gọi lại một lần với bản nháp
        await s.R.InputAsync(Editor, SourceContent + "\nQuy");
        await s.R.InputAsync(Editor, DraftContent);
        await WaitUntil(() => s.Api.AssembledBodies.Count == 2 && s.R.Text.Contains(AddedRule) && !s.R.Text.Contains("sẽ cập nhật khi bạn ngừng gõ"));
        Assert.Equal(DraftContent, s.Api.AssembledBodies.Last().GetProperty("systemPrompt").GetString());
        Assert.Contains("Bản nháp (chưa lưu)", s.R.Text);
        Assert.Empty(s.Api.PreviewBodies);
        NoPromptMutations(s.Api);
    }

    [Fact]
    public async Task Masking_disabled_409_hides_payload_and_shows_security_guidance()
    {
        var api = new A41Handler();
        await using var s = await OpenPageAsync(api);
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        await s.R.ClickAttrAsync("aria-controls", "pg-panel-payload");
        await WaitUntil(() => s.R.Text.Contains("[EMAIL]"));

        api.AssembledOverride = _ => A41Handler.Json(HttpStatusCode.Conflict, new
        {
            title = "Chưa bật che dữ liệu cá nhân", detail = "Gemini:MaskPersonalData đang tắt nên nội dung gửi Gemini chứa dữ liệu cá nhân gốc.", code = "PersonalDataMaskingDisabled"
        });
        await s.R.ChangeAsync("pg-app", 12);
        await WaitUntil(() => s.R.Text.Contains("Máy chủ đang tắt che thông tin cá nhân"));
        Assert.DoesNotContain("[EMAIL]", s.R.Text);
        Assert.DoesNotContain("Đã che thông tin cá nhân ·", s.R.Text);
        Assert.Contains("Gemini:MaskPersonalData = true", s.R.Text);
    }

    [Fact]
    public async Task Unmasked_success_response_is_never_displayed()
    {
        var api = new A41Handler
        {
            AssembledOverride = b => A41Handler.Json(HttpStatusCode.OK, new AssembledPromptDto
            {
                ApplicationId = b.GetProperty("applicationId").GetInt32(), PersonalDataMasked = false, UserPrompt = "Email: an.nguyen@example.com"
            })
        };
        await using var s = await OpenPageAsync(api);
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        await s.R.ClickAttrAsync("aria-controls", "pg-panel-payload");
        await WaitUntil(() => s.R.Text.Contains("Không hiển thị nội dung"));
        Assert.DoesNotContain("an.nguyen@example.com", s.R.Text);
    }

    [Fact]
    public async Task Payload_is_rendered_as_text_not_markup()
    {
        var api = new A41Handler
        {
            AssembledOverride = b => A41Handler.Json(HttpStatusCode.OK, new AssembledPromptDto
            {
                ApplicationId = b.GetProperty("applicationId").GetInt32(), PersonalDataMasked = true, SystemPrompt = "<b>đậm</b>",
                UserPrompt = "<img src=x onerror=alert(1)> [EMAIL]", Model = "m"
            })
        };
        await using var s = await OpenPageAsync(api);
        await OpenPlaygroundAsync(s);
        await ChooseAsync(s, 1, 11);
        await s.R.ClickAttrAsync("aria-controls", "pg-panel-payload");
        await WaitUntil(() => s.R.Text.Contains("<img src=x onerror=alert(1)>"));
        Assert.False(s.R.HasMarkup("<img"));
        Assert.False(s.R.HasMarkup("<b>đậm"));
    }

    // =====================================================================================
    // 5. Khung đứng riêng: bộ CV, phản hồi muộn, chưa có phiên bản
    // =====================================================================================

    private static ParameterView PlaygroundParameters(PromptDraft draft, int? setId, List<int>? changed = null) => ParameterView.FromDictionary(new Dictionary<string, object?>
    {
        [nameof(PromptPlayground.Draft)] = draft,
        [nameof(PromptPlayground.EvaluationSetId)] = setId,
        [nameof(PromptPlayground.OnEvaluationSetChanged)] = EventCallback.Factory.Create<int>(new object(), id => changed?.Add(id))
    });

    [Fact]
    public async Task Without_page_set_the_first_set_is_chosen_and_reported_to_the_page()
    {
        var api = new A41Handler();
        var (r, sp, _) = await RendererAsync(api, anonymous: false);
        await using var _ = r; await using var __ = sp;
        var draft = new PromptDraft();
        draft.Load(api.Versions[0]);
        var changed = new List<int>();
        await r.StartAsync(typeof(PromptPlayground), PlaygroundParameters(draft, null, changed));
        await WaitUntil(() => r.Text.Contains("/5 sẵn sàng") && changed.Count > 0);
        Assert.Equal([7], changed);
        Assert.Equal([7], api.CandidateSetIds);
    }

    [Fact]
    public async Task Page_set_change_reloads_labels_and_late_responses_of_the_old_set_are_ignored()
    {
        var api = new A41Handler();
        api.CandidateGates[7] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (r, sp, _) = await RendererAsync(api, anonymous: false);
        await using var _ = r; await using var __ = sp;
        var draft = new PromptDraft();
        draft.Load(api.Versions[0]);
        await r.StartAsync(typeof(PromptPlayground), PlaygroundParameters(draft, 7));
        await WaitUntil(() => api.CandidateSetIds.Contains(7));

        await r.SetParametersAsync(PlaygroundParameters(draft, 8));
        await WaitUntil(() => api.CandidateSetIds.Contains(8) && r.Text.Contains("Backend Developer (.NET) · 2/5"));
        api.CandidateGates[7].SetResult();   // phản hồi muộn của bộ 7 (có nhãn HR) đến sau
        await Task.Delay(100);
        await r.ChangeAsync("pg-job", 1);
        Assert.Equal("Nguyễn Văn An · HS11", r.OptionText("11"));   // không có "· HR: Đạt" của bộ 7
        Assert.Equal("8", r.SelectValue("pg-set"));
        Assert.Empty(r.Errors);
    }

    [Fact]
    public async Task Page_set_change_while_running_cancels_the_trial_and_never_applies_its_late_result()
    {
        var api = new A41Handler { PreviewGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (r, sp, _) = await RendererAsync(api, anonymous: false);
        await using var _ = r; await using var __ = sp;
        var draft = new PromptDraft();
        draft.Load(api.Versions[0]);
        await r.StartAsync(typeof(PromptPlayground), PlaygroundParameters(draft, 7));
        await WaitUntil(() => r.Text.Contains("/5 sẵn sàng"));
        await r.ChangeAsync("pg-job", 1);
        await r.ChangeAsync("pg-app", 11);
        var run = r.ClickAsync(RunVersion);
        await WaitUntil(() => api.PreviewBodies.Count == 1);
        var quotaReads = api.Count("GET /api/applications/ai-preview/quota");

        await r.SetParametersAsync(PlaygroundParameters(draft, 8));
        await run;
        api.PreviewGate.SetResult();
        await WaitUntil(() => api.CandidateSetIds.Contains(8) && api.Count("GET /api/applications/ai-preview/quota") > quotaReads);
        await Task.Delay(100);
        Assert.Equal("8", r.SelectValue("pg-set"));
        Assert.DoesNotContain("Chấm thử · không lưu", r.Text);
        Assert.False(r.Disabled(RunVersion));
        Assert.Empty(r.Errors);
    }

    [Fact]
    public async Task Without_any_version_the_draft_is_tested_and_empty_content_is_blocked()
    {
        var api = new A41Handler();
        var (r, sp, _) = await RendererAsync(api, anonymous: false);
        await using var _ = r; await using var __ = sp;
        var draft = new PromptDraft();
        draft.Load(null);
        await r.StartAsync(typeof(PromptPlayground), PlaygroundParameters(draft, 7));
        await WaitUntil(() => r.Text.Contains("/5 sẵn sàng"));
        await r.ChangeAsync("pg-job", 1);
        await r.ChangeAsync("pg-app", 11);
        Assert.True(r.Disabled(RunDraft));
        Assert.Contains("Nội dung prompt đang trống", r.Text);

        draft.Content = "Bạn là HR.";
        await r.SetParametersAsync(PlaygroundParameters(draft, 7));
        Assert.False(r.Disabled(RunDraft));
        await r.ClickAsync(RunDraft);
        Assert.Equal("Bạn là HR.", api.PreviewBodies.Single().GetProperty("systemPrompt").GetString());
        Assert.Contains("Chưa có phiên bản nào", r.Text);
    }

    [Fact]
    public async Task No_evaluation_set_shows_guidance_instead_of_null_errors()
    {
        var api = new A41Handler { Sets = [] };
        var (r, sp, _) = await RendererAsync(api, anonymous: false);
        await using var _ = r; await using var __ = sp;
        var draft = new PromptDraft();
        draft.Load(api.Versions[0]);
        await r.StartAsync(typeof(PromptPlayground), PlaygroundParameters(draft, null));
        await WaitUntil(() => r.Text.Contains("Chưa có bộ CV đánh giá"));
        Assert.Empty(api.CandidateSetIds);
        Assert.Contains("Tạo bộ CV đánh giá", r.Text);
        Assert.Empty(r.Errors);
    }
}
