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

/// <summary>B12: real Blazor page with a test-only A14 HTTP handler; no database writes or Gemini calls.</summary>
#pragma warning disable BL0006 // Renderer APIs used only by component interaction tests.
public class PromptVersioningUiTests
{
    private const string ActiveContent = "Bạn là chuyên gia tuyển dụng.\nChỉ dựa trên bằng chứng trong CV.";
    private const string OldContent = "Vai trò cũ.\nQuy tắc cũ.";
    private const string ChangedContent = ActiveContent + "\nCông nhận kinh nghiệm thực tập có bằng chứng.";
    private const string SaveButton = "Lưu thành phiên bản mới";

    private sealed class PromptNavigationManager : NavigationManager
    {
        public Task LastNavigation { get; private set; } = Task.CompletedTask;
        public PromptNavigationManager() => Initialize("https://app.test/", "https://app.test/prompts");
        protected override void SetNavigationLockState(bool value) { }
        protected override void HandleLocationChangingHandlerException(Exception exception, LocationChangingContext context) =>
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override void NavigateToCore(string uri, NavigationOptions options) => LastNavigation = NavigateAsync(uri, options);
        private async Task NavigateAsync(string uri, NavigationOptions options)
        {
            var target = ToAbsoluteUri(uri).ToString();
            if (!await NotifyLocationChangingAsync(target, options.HistoryEntryState, isNavigationIntercepted: false)) return;
            Uri = target;
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

    private sealed class PromptHttpHandler : HttpMessageHandler
    {
        public List<PromptVersionDto> Versions { get; } =
        [
            new() { PromptVersionId = 2, VersionName = "v2-tieu-chi", SystemPrompt = ActiveContent,
                ChangeNote = "Bổ sung bằng chứng", IsActive = true, HasRuns = true, LatestAccuracyPercent = 80,
                CreatedAt = new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc) },
            new() { PromptVersionId = 1, VersionName = "v1-co-ban", SystemPrompt = OldContent,
                ChangeNote = "Phiên bản đầu", HasRuns = true,
                CreatedAt = new DateTime(2026, 11, 9, 2, 0, 0, DateTimeKind.Utc) }
        ];
        public ConcurrentQueue<string> Calls { get; } = new();
        public List<CreatePromptVersionRequest> Requests { get; } = [];
        public string? SaveError { get; set; }
        public bool FailLoad { get; set; }
        public TaskCompletionSource? SaveGate { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            Calls.Enqueue($"{request.Method} {path}");
            if (path != "/api/prompts") return Json(HttpStatusCode.NotFound, new { title = "Unexpected route" });
            if (request.Method == HttpMethod.Get)
                return FailLoad ? Json(HttpStatusCode.ServiceUnavailable, new { detail = "Không tải được danh sách." })
                    : Json(HttpStatusCode.OK, Versions.ToList());
            if (request.Method != HttpMethod.Post) return Json(HttpStatusCode.MethodNotAllowed, new { title = "Unexpected mutation" });
            var body = await request.Content!.ReadFromJsonAsync<CreatePromptVersionRequest>(ct);
            Requests.Add(body!);
            if (SaveGate is not null) await SaveGate.Task.WaitAsync(ct);
            if (SaveError is not null) return Json(HttpStatusCode.Conflict, new { title = "Không thể lưu", detail = SaveError });
            var created = new PromptVersionDto
            {
                PromptVersionId = Versions.Select(v => v.PromptVersionId).DefaultIfEmpty(0).Max() + 1,
                VersionName = body!.VersionName, SystemPrompt = body.SystemPrompt, ChangeNote = body.ChangeNote,
                IsActive = false, HasRuns = false, LatestAccuracyPercent = null, CreatedAt = DateTime.UtcNow
            };
            Versions.Insert(0, created);
            return Json(HttpStatusCode.Created, created);
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
            foreach (var frame in frames.Where(f => f.FrameType == RenderTreeFrameType.Component))
                all.AddRange(Components(frame.ComponentId));
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

        private static string? Attr(RenderTreeFrame[] frames, int element, string name)
        {
            for (var j = element + 1; j < frames.Length && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
                if (frames[j].AttributeName == name) return frames[j].AttributeValue?.ToString();
            return null;
        }

        private static string Inner(RenderTreeFrame[] frames, int element) =>
            Clean(frames.Skip(element + 1).Take(frames[element].ElementSubtreeLength - 1));

        private (RenderTreeFrame[] Frames, int Index) Find(Func<RenderTreeFrame[], int, bool> match)
        {
            foreach (var frames in Components(rootId))
                for (var i = 0; i < frames.Length; i++)
                    if (frames[i].FrameType == RenderTreeFrameType.Element && match(frames, i)) return (frames, i);
            throw new Xunit.Sdk.XunitException("Không tìm thấy phần tử tương tác trên trang Prompt.");
        }

        private ulong Handler(Func<RenderTreeFrame[], int, bool> match, string eventName)
        {
            var (frames, i) = Find(match);
            for (var j = i + 1; j < frames.Length && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
                if (frames[j].AttributeName == eventName) return frames[j].AttributeEventHandlerId;
            throw new Xunit.Sdk.XunitException($"Không tìm thấy sự kiện {eventName}.");
        }

        public Task InputAsync(string label, string content) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Handler((f, i) => (f[i].ElementName is "input" or "textarea") && Attr(f, i, "aria-label") == label, "oninput"),
            null, new ChangeEventArgs { Value = content }));

        public string Value(string label) => Dispatcher.InvokeAsync(() =>
        {
            var (frames, i) = Find((f, n) => (f[n].ElementName is "input" or "textarea") && Attr(f, n, "aria-label") == label);
            return Attr(frames, i, "value") ?? Inner(frames, i);
        }).Result;

        public Task ClickAsync(string text) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Handler((f, i) => f[i].ElementName == "button" && Inner(f, i).Contains(text, StringComparison.Ordinal), "onclick"),
            null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
    }

    private static async Task<(TestRenderer Renderer, ServiceProvider Services)> OpenAsync(PromptHttpHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new PromptNavigationManager());
        services.AddSingleton<IJSRuntime>(new NoopJsRuntime());
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.test/") };
        services.AddSingleton(new PromptApiClient(http, NullLogger<PromptApiClient>.Instance));
        var provider = services.BuildServiceProvider();
        var renderer = new TestRenderer(provider);
        await renderer.StartAsync();
        return (renderer, provider);
    }

    private static async Task PrepareAsync(TestRenderer renderer, string name = "v3-thuc-tap")
    {
        await renderer.InputAsync("Nội dung system prompt", ChangedContent);
        await renderer.InputAsync("Tên phiên bản mới", name);
        await renderer.InputAsync("Ghi chú thay đổi", "Ghi nhận kinh nghiệm thực tập.");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task Open_page_loads_active_prompt_history_and_actual_accuracy_without_other_features()
    {
        var api = new PromptHttpHandler();
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        Assert.Equal(ActiveContent, r.Value("Nội dung system prompt"));
        Assert.Contains("v2-tieu-chi", r.Text);
        Assert.Contains("v1-co-ban", r.Text);
        Assert.Contains("Đang dùng", r.Text);
        Assert.Contains("80%", r.Text);
        Assert.Contains("—", r.Text);
        Assert.Contains("Chưa đánh giá", r.Text);
        Assert.Contains("10/11/2026 09:00", r.Text);
        Assert.Contains("Kích hoạt", r.Text); // B13 adds activation alongside the B12 editor.
        Assert.DoesNotContain("Chạy đánh giá", r.Text);
        Assert.Equal(new[] { "GET /api/prompts" }, api.Calls);
    }

    [Fact]
    public async Task Failed_initial_get_shows_error_and_retry_loads_real_history()
    {
        var api = new PromptHttpHandler { FailLoad = true };
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        Assert.Contains("Không tải được phiên bản prompt", r.Text);
        Assert.Contains("Không tải được danh sách", r.Text);
        api.FailLoad = false;
        await r.ClickAsync("Thử lại");
        Assert.Equal(ActiveContent, r.Value("Nội dung system prompt"));
        Assert.Contains("v1-co-ban", r.Text);
        Assert.Equal(2, api.Calls.Count(c => c == "GET /api/prompts"));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Adding_line_updates_counts_and_undo_restores_selected_source()
    {
        var api = new PromptHttpHandler();
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.InputAsync("Nội dung system prompt", ChangedContent);
        Assert.Contains("3 dòng", r.Text);
        Assert.Contains("+1", r.Text);
        Assert.Contains("−0", r.Text);
        await r.ClickAsync("Hoàn tác");
        Assert.Equal(ActiveContent, r.Value("Nội dung system prompt"));
        await r.ClickAsync("v1-co-ban");
        Assert.Equal(OldContent, r.Value("Nội dung system prompt"));
        await r.InputAsync("Nội dung system prompt", "Bản nháp khác.");
        await r.ClickAsync("Hoàn tác");
        Assert.Equal(OldContent, r.Value("Nội dung system prompt"));
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("v3-", "chuyên gia", "tên")]
    [InlineData(" V2-TIEU-CHI ", "chuyên gia", "phiên bản")]
    [InlineData("v3-moi", "  \n  ", "trống")]
    [InlineData("v3-moi", ActiveContent, "bản gốc")]
    public async Task Invalid_drafts_do_not_send_post_and_keep_content(string name, string content, string expectedError)
    {
        var api = new PromptHttpHandler();
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.InputAsync("Nội dung system prompt", content);
        await r.InputAsync("Tên phiên bản mới", name);
        await r.ClickAsync(SaveButton);
        Assert.Empty(api.Requests);
        Assert.Contains(expectedError, r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(content, r.Value("Nội dung system prompt"));
    }

    [Fact]
    public async Task Save_creates_new_version_preserves_source_and_survives_page_reload()
    {
        var api = new PromptHttpHandler();
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await PrepareAsync(r);
        await r.ClickAsync(SaveButton);
        Assert.Single(api.Requests);
        Assert.Equal("v3-thuc-tap", api.Requests[0].VersionName);
        Assert.Equal(ChangedContent, api.Requests[0].SystemPrompt);
        Assert.Equal("Ghi nhận kinh nghiệm thực tập.", api.Requests[0].ChangeNote);
        Assert.Equal(ActiveContent, api.Versions.Single(v => v.PromptVersionId == 2).SystemPrompt);
        Assert.True(api.Versions.Single(v => v.PromptVersionId == 2).IsActive);
        var created = api.Versions.Single(v => v.VersionName == "v3-thuc-tap");
        Assert.False(created.IsActive);
        Assert.False(created.HasRuns);
        Assert.Null(created.LatestAccuracyPercent);
        Assert.Contains("Đã lưu", r.Text);
        Assert.Equal(ChangedContent, r.Value("Nội dung system prompt"));
        await r.ClickAsync("v1-co-ban");
        Assert.Equal(OldContent, r.Value("Nội dung system prompt"));

        var (reload, reloadServices) = await OpenAsync(api);
        await using var ___ = reload; using var ____ = reloadServices;
        Assert.Contains("v3-thuc-tap", reload.Text);
        await reload.ClickAsync("v3-thuc-tap");
        Assert.Equal(ChangedContent, reload.Value("Nội dung system prompt"));
        Assert.All(api.Calls, call => Assert.Contains("/api/prompts", call));
        Assert.DoesNotContain(api.Calls, call => call.Contains("activate") || call.Contains("prompt-tests"));
    }

    [Fact]
    public async Task Api_save_failure_preserves_draft_and_retry_can_succeed()
    {
        var api = new PromptHttpHandler { SaveError = "Tên phiên bản vừa được sử dụng. Hãy chọn tên khác." };
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await PrepareAsync(r);
        await r.ClickAsync(SaveButton);
        Assert.Equal(ChangedContent, r.Value("Nội dung system prompt"));
        Assert.Equal("v3-thuc-tap", r.Value("Tên phiên bản mới"));
        Assert.Contains("Tên phiên bản vừa được sử dụng", r.Text);
        Assert.DoesNotContain("Đã lưu", r.Text);
        api.SaveError = null;
        await r.ClickAsync(SaveButton);
        Assert.Contains("Đã lưu", r.Text);
        Assert.Equal(2, api.Requests.Count);
        Assert.Single(api.Versions, v => v.VersionName == "v3-thuc-tap");
    }

    [Fact]
    public async Task Double_click_while_post_is_pending_sends_only_one_create_request()
    {
        var api = new PromptHttpHandler { SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await PrepareAsync(r);
        var pending = r.ClickAsync(SaveButton);
        await WaitUntil(() => api.Requests.Count == 1);
        try
        {
            // Dispatching again also checks the handler guard, beyond browser disabled behavior.
            await r.ClickAsync("Đang lưu");
            Assert.Single(api.Requests);
        }
        finally { api.SaveGate.SetResult(); }
        await pending;
        Assert.Single(api.Requests);
        Assert.Single(api.Versions, v => v.VersionName == "v3-thuc-tap");
    }

    [Fact]
    public async Task Switching_versions_requires_explicit_discard_and_can_keep_draft()
    {
        var api = new PromptHttpHandler();
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await PrepareAsync(r);
        await r.ClickAsync("v1-co-ban");
        Assert.Equal(ChangedContent, r.Value("Nội dung system prompt"));
        await r.ClickAsync("Giữ bản nháp");
        Assert.Equal(ChangedContent, r.Value("Nội dung system prompt"));
        await r.ClickAsync("v1-co-ban");
        await r.ClickAsync("Bỏ bản nháp và chuyển");
        Assert.Equal(OldContent, r.Value("Nội dung system prompt"));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Internal_navigation_preserves_unsaved_draft_until_discard_is_confirmed()
    {
        var api = new PromptHttpHandler();
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        var nav = (PromptNavigationManager)sp.GetRequiredService<NavigationManager>();
        await PrepareAsync(r);
        await r.Dispatcher.InvokeAsync(() => nav.NavigateTo("results"));
        await nav.LastNavigation;
        Assert.Equal("https://app.test/prompts", nav.Uri);
        Assert.Equal(ChangedContent, r.Value("Nội dung system prompt"));
        await r.ClickAsync("Giữ bản nháp");
        Assert.Equal("https://app.test/prompts", nav.Uri);
        await r.Dispatcher.InvokeAsync(() => nav.NavigateTo("results"));
        await nav.LastNavigation;
        await r.ClickAsync("Bỏ bản nháp và chuyển");
        await nav.LastNavigation;
        Assert.Equal("https://app.test/results", nav.Uri);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task First_prompt_can_be_created_from_empty_history_without_fake_seed()
    {
        var api = new PromptHttpHandler();
        api.Versions.Clear();
        var (r, sp) = await OpenAsync(api);
        await using var _ = r; using var __ = sp;
        await r.InputAsync("Nội dung system prompt", "Bạn là chuyên gia. Chỉ dùng bằng chứng trong CV.");
        await r.InputAsync("Tên phiên bản mới", "v1-khoi-dau");
        await r.ClickAsync(SaveButton);
        Assert.Single(api.Versions);
        Assert.Contains("v1-khoi-dau", r.Text);
        Assert.False(api.Versions[0].IsActive);
    }
}
#pragma warning restore BL0006
