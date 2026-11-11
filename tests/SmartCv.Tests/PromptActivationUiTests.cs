extern alias web;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>B13: actual page and HTTP client, with isolated server state in a test-only handler.</summary>
#pragma warning disable BL0006 // RenderTree APIs are used only for component interaction tests.
public class PromptActivationUiTests
{
    private const string V1 = "v1-co-ban";
    private const string V2 = "v2-tieu-chi";
    private const string V3 = "v3-chua-danh-gia";
    private const string SourceContent = "Bạn là chuyên gia tuyển dụng.\nChỉ dựa trên bằng chứng trong CV.";
    private const string DraftContent = SourceContent + "\nCông nhận kinh nghiệm thực tập có bằng chứng.";

    private static PromptVersionDto Version(int id, string name, bool active, decimal? accuracy) => new()
    {
        PromptVersionId = id, VersionName = name, SystemPrompt = id == 2 ? SourceContent : $"Quy tắc của {name}.",
        ChangeNote = $"Ghi chú {name}", IsActive = active, HasRuns = accuracy is not null,
        LatestAccuracyPercent = accuracy, CreatedAt = new DateTime(2026, 11, 8 + id, 2, 0, 0, DateTimeKind.Utc)
    };

    private static PromptVersionDto WithActive(PromptVersionDto version, bool active) => new()
    {
        PromptVersionId = version.PromptVersionId, VersionName = version.VersionName,
        SystemPrompt = version.SystemPrompt, ChangeNote = version.ChangeNote, IsActive = active,
        HasRuns = version.HasRuns, LatestAccuracyPercent = version.LatestAccuracyPercent, CreatedAt = version.CreatedAt
    };

    private sealed class PromptNavigationManager : NavigationManager
    {
        public PromptNavigationManager() => Initialize("https://app.test/", "https://app.test/prompts");
        protected override void SetNavigationLockState(bool value) { }
        protected override void HandleLocationChangingHandlerException(Exception exception, LocationChangingContext context) =>
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Uri = ToAbsoluteUri(uri).ToString();
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }

    private sealed class NoopJsRuntime : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(typeof(TValue) == typeof(IJSObjectReference) ? (TValue)(object)this : default!);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ActivationHttpHandler : HttpMessageHandler
    {
        public List<PromptVersionDto> Versions { get; } = [Version(3, V3, false, null), Version(2, V2, true, 80), Version(1, V1, false, 70)];
        public ConcurrentQueue<string> Calls { get; } = new();
        public TaskCompletionSource? ActivationGate { get; set; }
        public TaskCompletionSource? ReloadGate { get; set; }
        public bool FailActivation { get; set; }
        public bool ApplyThenLoseResponse { get; set; }
        public bool MalformedActivationResponse { get; set; }
        public bool FailReload { get; set; }
        public int? ConcurrentActiveId { get; set; }
        public bool Activated { get; private set; }
        public int ActivationCalls => Calls.Count(c => c.StartsWith("POST ", StringComparison.Ordinal) && c.EndsWith("/activate", StringComparison.Ordinal));

        public void SetActive(int id)
        {
            for (var i = 0; i < Versions.Count; i++) Versions[i] = WithActive(Versions[i], Versions[i].PromptVersionId == id);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            Calls.Enqueue($"{request.Method} {path}");
            if (request.Method == HttpMethod.Get && path == "/api/prompts")
            {
                if (Activated && ReloadGate is not null) await ReloadGate.Task.WaitAsync(ct);
                return Activated && FailReload
                    ? Json(HttpStatusCode.ServiceUnavailable, new { detail = "Không đọc được trạng thái mới từ máy chủ." })
                    : Json(HttpStatusCode.OK, Versions.ToList());
            }
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (request.Method != HttpMethod.Post || parts.Length != 4 || parts[0] != "api" || parts[1] != "prompts"
                || parts[3] != "activate" || !int.TryParse(parts[2], out var id))
                return Json(HttpStatusCode.NotFound, new { detail = "Test received an unexpected API operation." });
            if (ActivationGate is not null) await ActivationGate.Task.WaitAsync(ct);
            if (FailActivation) return Json(HttpStatusCode.Conflict, new { detail = "Một yêu cầu khác đang kích hoạt prompt. Hãy thử lại." });
            if (!Versions.Any(v => v.PromptVersionId == id)) return Json(HttpStatusCode.NotFound, new { detail = "Không tìm thấy phiên bản." });
            SetActive(id);
            Activated = true;
            if (ApplyThenLoseResponse) throw new HttpRequestException("The connection was lost after the server committed activation.");
            if (MalformedActivationResponse) return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ malformed", System.Text.Encoding.UTF8, "application/json")
            };
            var result = Versions.Single(v => v.PromptVersionId == id);
            if (ConcurrentActiveId is { } otherId) SetActive(otherId);
            return Json(HttpStatusCode.OK, result);
        }

        private static HttpResponseMessage Json<T>(HttpStatusCode status, T body) => new(status) { Content = JsonContent.Create(body) };
    }

    private sealed class TestRenderer : Renderer
    {
        public TestRenderer(IServiceProvider services) : base(services, NullLoggerFactory.Instance) =>
            ElementReferenceContext = new WebElementReferenceContext(services.GetRequiredService<IJSRuntime>());
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", isInteractive: true);
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        private int rootId;

        public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
        {
            rootId = AssignRootComponentId(InstantiateComponent(typeof(Prompts)));
            await RenderRootComponentAsync(rootId);
        });

        private List<RenderTreeFrame[]> Components(int id)
        {
            var range = GetCurrentRenderTreeFrames(id);
            var frames = range.Array.Take(range.Count).ToArray();
            var all = new List<RenderTreeFrame[]> { frames };
            foreach (var f in frames.Where(f => f.FrameType == RenderTreeFrameType.Component)) all.AddRange(Components(f.ComponentId));
            return all;
        }

        private static string Clean(IEnumerable<RenderTreeFrame> frames) => System.Text.RegularExpressions.Regex.Replace(
            WebUtility.HtmlDecode(string.Concat(frames.Select(f => f.FrameType switch
            {
                RenderTreeFrameType.Text => f.TextContent,
                RenderTreeFrameType.Markup => System.Text.RegularExpressions.Regex.Replace(f.MarkupContent, "<[^>]+>", " "),
                _ => " "
            }))), @"\s+", " ").Trim();

        private static string? Attr(RenderTreeFrame[] frames, int element, string name)
        {
            for (var j = element + 1; j < frames.Length && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
                if (frames[j].AttributeName == name) return frames[j].AttributeValue?.ToString();
            return null;
        }
        private static bool HasClass(RenderTreeFrame[] f, int i, string css) => (Attr(f, i, "class") ?? "").Split(' ').Contains(css);
        private static string Inner(RenderTreeFrame[] frames, int i) => Clean(frames.Skip(i + 1).Take(frames[i].ElementSubtreeLength - 1));
        private IEnumerable<(RenderTreeFrame[] Frames, int Index)> Elements()
        {
            foreach (var frames in Components(rootId))
                for (var i = 0; i < frames.Length; i++)
                    if (frames[i].FrameType == RenderTreeFrameType.Element) yield return (frames, i);
        }
        private (RenderTreeFrame[] Frames, int Index) Find(Func<RenderTreeFrame[], int, bool> match) =>
            Elements().First(e => match(e.Frames, e.Index));
        private ulong Handler(Func<RenderTreeFrame[], int, bool> match, string name)
        {
            var (frames, i) = Find(match);
            for (var j = i + 1; j < frames.Length && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
                if (frames[j].AttributeName == name) return frames[j].AttributeEventHandlerId;
            throw new Xunit.Sdk.XunitException($"Missing event {name}.");
        }
        public string Text => Dispatcher.InvokeAsync(() => string.Join(" ", Components(rootId).Select(Clean))).Result;
        public string ClassText(string css) => Dispatcher.InvokeAsync(() => string.Join(" ", Elements()
            .Where(e => HasClass(e.Frames, e.Index, css)).Select(e => Inner(e.Frames, e.Index)))).Result;
        public int ClassCount(string css) => Dispatcher.InvokeAsync(() =>
            Elements().Count(e => HasClass(e.Frames, e.Index, css))
            // Razor emits static badges as Markup frames rather than Element/Attribute frames.
            + Components(rootId).SelectMany(f => f).Where(f => f.FrameType == RenderTreeFrameType.Markup)
                .Sum(f => System.Text.RegularExpressions.Regex.Matches(f.MarkupContent, "\\bclass\\s*=\\s*[\"']([^\"']*)[\"']")
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Count(m => m.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(css)))).Result;
        public bool HasActivation(string name) => Dispatcher.InvokeAsync(() => Elements().Any(e =>
            e.Frames[e.Index].ElementName == "button" && Attr(e.Frames, e.Index, "aria-label") == $"Kích hoạt {name}")).Result;
        public bool ActivationDisabled(string name) => Dispatcher.InvokeAsync(() =>
        {
            var (f, i) = Find((frames, index) => frames[index].ElementName == "button" && Attr(frames, index, "aria-label") == $"Kích hoạt {name}");
            return Attr(f, i, "disabled") is "True" or "true" or "disabled";
        }).Result;
        public string SelectedVersion => Dispatcher.InvokeAsync(() => Elements().Where(e =>
            HasClass(e.Frames, e.Index, "history-pick") && Attr(e.Frames, e.Index, "aria-pressed") == "true")
            .Select(e => Inner(e.Frames, e.Index)).SingleOrDefault() ?? "").Result;
        public string Value(string label) => Dispatcher.InvokeAsync(() =>
        {
            var (f, i) = Find((frames, index) => (frames[index].ElementName is "input" or "textarea") && Attr(frames, index, "aria-label") == label);
            return Attr(f, i, "value") ?? Inner(f, i);
        }).Result;
        public Task InputAsync(string label, string value) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Handler((f, i) => (f[i].ElementName is "input" or "textarea") && Attr(f, i, "aria-label") == label, "oninput"),
            null, new ChangeEventArgs { Value = value }));
        public Task ActivateAsync(string name) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Handler((f, i) => f[i].ElementName == "button" && Attr(f, i, "aria-label") == $"Kích hoạt {name}", "onclick"),
            null, new MouseEventArgs()));
        public Task ClickAsync(string text) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Handler((f, i) => f[i].ElementName == "button" && Inner(f, i).Contains(text, StringComparison.Ordinal), "onclick"),
            null, new MouseEventArgs()));
    }

    private static async Task<(TestRenderer Renderer, ServiceProvider Services)> OpenAsync(ActivationHttpHandler api)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<NavigationManager>(new PromptNavigationManager());
        services.AddSingleton<IJSRuntime>(new NoopJsRuntime());
        services.AddSingleton(new PromptApiClient(new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("https://api.test/") },
            NullLogger<PromptApiClient>.Instance));
        var provider = services.BuildServiceProvider();
        var renderer = new TestRenderer(provider);
        await renderer.StartAsync();
        return (renderer, provider);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task Initial_state_distinguishes_selected_active_and_unrated_versions()
    {
        var api = new ActivationHttpHandler();
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        Assert.Contains(V2, r.ClassText("active-version"));
        Assert.Contains("80%", r.ClassText("active-version"));
        Assert.Contains(V2, r.SelectedVersion);
        Assert.False(r.HasActivation(V2));
        Assert.True(r.HasActivation(V1));
        Assert.True(r.HasActivation(V3));
        Assert.Equal(1, r.ClassCount("tag-active"));
        Assert.Contains("—", r.Text);
        await r.ClickAsync(V1);
        Assert.Contains(V1, r.SelectedVersion);
        Assert.Contains(V2, r.ClassText("active-version"));
        Assert.Equal(0, api.ActivationCalls);
    }

    [Fact]
    public async Task Initial_server_list_with_multiple_active_versions_is_rejected_without_misleading_active_ui()
    {
        var api = new ActivationHttpHandler();
        api.Versions[2] = WithActive(api.Versions[2], true);
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        Assert.Contains("API trả về nhiều phiên bản đang dùng", r.Text);
        Assert.Equal(0, r.ClassCount("active-version"));
        Assert.Equal(0, r.ClassCount("prompt-history"));
        Assert.Equal(0, r.ClassCount("tag-active"));
        Assert.Equal(0, api.ActivationCalls);
    }

    [Fact]
    public async Task Activation_in_both_directions_reloads_server_and_preserves_old_content_and_notes()
    {
        var api = new ActivationHttpHandler();
        var before = api.Versions.ToDictionary(v => v.PromptVersionId, v => (v.SystemPrompt, v.ChangeNote, v.HasRuns, v.CreatedAt));
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.ActivateAsync(V1);
        Assert.Contains($"Đã kích hoạt {V1}", r.Text);
        Assert.Contains(V1, r.ClassText("active-version"));
        Assert.Contains("70%", r.ClassText("active-version"));
        Assert.False(r.HasActivation(V1));
        Assert.True(r.HasActivation(V2));
        Assert.Equal(1, r.ClassCount("tag-active"));
        Assert.Equal(new[] { "GET /api/prompts", "POST /api/prompts/1/activate", "GET /api/prompts" }, api.Calls.Where(c => c.Contains("/api/prompts")));
        await r.ActivateAsync(V2);
        Assert.Contains($"Đã kích hoạt {V2}", r.Text);
        Assert.Contains(V2, r.ClassText("active-version"));
        Assert.Equal(1, r.ClassCount("tag-active"));
        Assert.Single(api.Versions, v => v.IsActive);
        foreach (var v in api.Versions) Assert.Equal(before[v.PromptVersionId], (v.SystemPrompt, v.ChangeNote, v.HasRuns, v.CreatedAt));
        // B16 may read run history; activation must never create a PromptTest or Screening run.
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("POST /api/prompt-tests", StringComparison.Ordinal) || c.Contains("/api/runs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Active_state_survives_new_page_load_and_null_accuracy_is_not_zero()
    {
        var api = new ActivationHttpHandler();
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.ActivateAsync(V3);
        Assert.Contains(V3, r.ClassText("active-version"));
        Assert.Contains("—", r.ClassText("active-version"));
        Assert.DoesNotContain("0%", r.ClassText("active-version"));
        var (reload, provider) = await OpenAsync(api);
        await using var ___ = reload; using var ____ = provider;
        Assert.Contains(V3, reload.ClassText("active-version"));
        Assert.False(reload.HasActivation(V3));
        Assert.Contains(V3, reload.SelectedVersion);
        Assert.Equal(1, reload.ClassCount("tag-active"));
    }

    [Fact]
    public async Task Double_click_and_other_activation_buttons_cannot_send_parallel_requests()
    {
        var api = new ActivationHttpHandler { ActivationGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        var pending = r.ActivateAsync(V1);
        await WaitUntil(() => api.ActivationCalls == 1);
        try
        {
            Assert.Contains("Đang kích hoạt", r.Text);
            Assert.True(r.ActivationDisabled(V1));
            Assert.True(r.ActivationDisabled(V3));
            Assert.Contains(V2, r.ClassText("active-version"));
            Assert.DoesNotContain("Đã kích hoạt", r.Text);
            await r.ActivateAsync(V1);
            await r.ActivateAsync(V3);
            Assert.Equal(1, api.ActivationCalls);
        }
        finally { api.ActivationGate.SetResult(); }
        await pending;
        Assert.Contains(V1, r.ClassText("active-version"));
        Assert.False(r.ActivationDisabled(V3));
    }

    [Fact]
    public async Task Success_is_not_shown_until_followup_get_confirms_activation()
    {
        var api = new ActivationHttpHandler { ReloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        var pending = r.ActivateAsync(V1);
        await WaitUntil(() => api.Activated && api.Calls.Count(c => c == "GET /api/prompts") == 2);
        try
        {
            Assert.DoesNotContain("Đã kích hoạt", r.Text);
            Assert.Contains(V2, r.ClassText("active-version"));
            Assert.True(r.ActivationDisabled(V3));
        }
        finally { api.ReloadGate.SetResult(); }
        await pending;
        Assert.Contains($"Đã kích hoạt {V1}", r.Text);
    }

    [Fact]
    public async Task Api_rejection_does_not_change_active_and_buttons_allow_retry()
    {
        var api = new ActivationHttpHandler { FailActivation = true };
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.ActivateAsync(V1);
        Assert.DoesNotContain("Đã kích hoạt", r.Text);
        Assert.Contains("Một yêu cầu khác", r.Text);
        Assert.Contains(V2, r.ClassText("active-version"));
        Assert.False(r.ActivationDisabled(V1));
        Assert.Equal(2, api.Calls.Count(c => c == "GET /api/prompts"));
        api.FailActivation = false;
        await r.ActivateAsync(V1);
        Assert.Contains($"Đã kích hoạt {V1}", r.Text);
        Assert.Equal(2, api.ActivationCalls);
    }

    [Fact]
    public async Task Lost_response_after_commit_reconciles_from_server_without_false_success()
    {
        var api = new ActivationHttpHandler { ApplyThenLoseResponse = true };
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.ActivateAsync(V1);
        Assert.DoesNotContain("Đã kích hoạt", r.Text);
        Assert.Contains(V1, r.ClassText("active-version"));
        Assert.Equal(1, r.ClassCount("tag-active"));
        Assert.False(r.HasActivation(V1));
        Assert.Equal(2, api.Calls.Count(c => c == "GET /api/prompts"));
    }

    [Fact]
    public async Task Malformed_success_response_after_commit_reconciles_without_claiming_confirmed_success()
    {
        var api = new ActivationHttpHandler { MalformedActivationResponse = true };
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.ActivateAsync(V1);
        Assert.DoesNotContain("Đã kích hoạt", r.Text);
        Assert.Contains("không đúng định dạng", r.Text);
        Assert.Contains(V1, r.ClassText("active-version"));
        Assert.Equal(1, r.ClassCount("tag-active"));
        Assert.False(r.HasActivation(V1));
        Assert.Equal(1, api.ActivationCalls);
        Assert.Equal(2, api.Calls.Count(c => c == "GET /api/prompts"));
    }

    [Fact]
    public async Task Failed_followup_get_blocks_mutations_until_explicit_sync_recovers()
    {
        var api = new ActivationHttpHandler { FailReload = true };
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.ActivateAsync(V1);
        Assert.DoesNotContain("Đã kích hoạt", r.Text);
        Assert.Contains("Đồng bộ trạng thái", r.Text);
        Assert.True(r.ActivationDisabled(V3));
        await r.ActivateAsync(V3);
        Assert.Equal(1, api.ActivationCalls);
        api.FailReload = false;
        await r.ClickAsync("Đồng bộ trạng thái");
        Assert.Contains(V1, r.ClassText("active-version"));
        Assert.False(r.ActivationDisabled(V3));
        Assert.Equal(1, api.ActivationCalls);
    }

    [Fact]
    public async Task Activation_preserves_selected_source_and_every_unsaved_draft_field()
    {
        var api = new ActivationHttpHandler();
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.InputAsync("Nội dung system prompt", DraftContent);
        await r.InputAsync("Tên phiên bản mới", "v4-ban-nhap");
        await r.InputAsync("Ghi chú thay đổi", "Ghi chú đang soạn.");
        await r.ActivateAsync(V1);
        Assert.Equal(DraftContent, r.Value("Nội dung system prompt"));
        Assert.Equal("v4-ban-nhap", r.Value("Tên phiên bản mới"));
        Assert.Equal("Ghi chú đang soạn.", r.Value("Ghi chú thay đổi"));
        Assert.Contains(V2, r.SelectedVersion);
        Assert.Contains(V1, r.ClassText("active-version"));
        Assert.DoesNotContain("Đang dùng", r.ClassText("editor-title"));
        await r.ClickAsync("Hoàn tác");
        Assert.Equal(SourceContent, r.Value("Nội dung system prompt"));
        Assert.Equal(string.Empty, r.Value("Tên phiên bản mới"));
        Assert.Equal(string.Empty, r.Value("Ghi chú thay đổi"));
        Assert.Contains(V2, r.SelectedVersion);
    }

    [Fact]
    public async Task Concurrent_server_activation_uses_current_get_state_instead_of_post_response()
    {
        var api = new ActivationHttpHandler { ConcurrentActiveId = 3 };
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.ActivateAsync(V1);
        Assert.Contains(V3, r.ClassText("active-version"));
        Assert.DoesNotContain($"Đã kích hoạt {V1}", r.Text);
        Assert.Equal(1, r.ClassCount("tag-active"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("single-active")]
    [InlineData("single-inactive")]
    public async Task Empty_or_single_version_handles_active_state_without_null_errors(string scenario)
    {
        var api = new ActivationHttpHandler();
        api.Versions.Clear();
        if (scenario != "empty") api.Versions.Add(Version(1, V1, scenario == "single-active", null));
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        Assert.Equal(scenario == "single-active" ? 1 : 0, r.ClassCount("tag-active"));
        Assert.Equal(scenario == "single-inactive", r.HasActivation(V1));
        Assert.False(r.HasActivation(V2));
        if (scenario == "single-inactive")
        {
            await r.ActivateAsync(V1);
            Assert.Contains(V1, r.ClassText("active-version"));
            Assert.Equal(1, r.ClassCount("tag-active"));
        }
    }
}
#pragma warning restore BL0006
