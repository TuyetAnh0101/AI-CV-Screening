extern alias web;

using System.Collections.Concurrent;
using System.Diagnostics;
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
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
using SetsPage = web::SmartCv.Web.Components.Pages.EvaluationSets;
using PromptsPage = web::SmartCv.Web.Components.Pages.Prompts;

namespace SmartCv.Tests.B25Ui;

/// <summary>
/// B25: trang quản lý bộ đánh giá /evaluation-sets — độ tin cậy của bộ (C8), một EvaluationSetManager duy nhất,
/// lượt Prompt Test theo bộ với cảnh báo phiên bản bộ, khóa rời trang khi còn nhãn chưa lưu, Prompt Test chỉ chạy ở /prompts.
/// </summary>
#pragma warning disable BL0006
public class B25EvaluationSetsTests
{
    // ---------------- EvaluationSetHealth (thuần) ----------------

    [Fact]
    public void Health_empty_set_has_no_label() => Assert.Equal(SetHealthLevel.Empty, EvaluationSetHealth.Assess(0, 0).Level);

    [Fact]
    public void Health_50_balanced_many_jobs_is_good()
    {
        var h = EvaluationSetHealth.Assess(50, 24, jobCount: 3);
        Assert.Equal(SetHealthLevel.Good, h.Level);
        Assert.Empty(h.Issues);
        Assert.Equal(48m, h.MinorityPercent);
        Assert.Equal("Đủ tin cậy", h.LevelText);
    }

    [Fact]
    public void Health_under_30_is_poor()
    {
        var h = EvaluationSetHealth.Assess(20, 10, jobCount: 2);
        Assert.Equal(SetHealthLevel.Poor, h.Level);
        Assert.Contains(h.Issues, i => i.Severity == IssueSeverity.Severe && i.Text.StartsWith("Quá nhỏ"));
    }

    [Fact]
    public void Health_30_to_49_needs_caution() =>
        Assert.Equal(SetHealthLevel.Caution, EvaluationSetHealth.Assess(40, 20, jobCount: 2).Level);

    [Fact]
    public void Health_severe_skew_is_poor_and_flags_misses()
    {
        var h = EvaluationSetHealth.Assess(60, 6, jobCount: 2);      // 10% Đạt
        Assert.Equal(SetHealthLevel.Poor, h.Level);
        Assert.Contains(h.Issues, i => i.Severity == IssueSeverity.Severe && i.Text.Contains("10% Đạt"));
        Assert.Contains(h.Issues, i => i.Text.Contains("bỏ sót"));    // chỉ 6 CV Đạt
    }

    [Fact]
    public void Health_mild_skew_needs_caution()
    {
        var h = EvaluationSetHealth.Assess(60, 45, jobCount: 2);     // 25% Không đạt
        Assert.Equal(SetHealthLevel.Caution, h.Level);
        Assert.Contains(h.Issues, i => i.Text.Contains("25% Không đạt"));
    }

    [Fact]
    public void Health_single_job_is_information_only()
    {
        var h = EvaluationSetHealth.Assess(50, 25, jobCount: 1);
        Assert.Equal(SetHealthLevel.Good, h.Level);
        var issue = Assert.Single(h.Issues);
        Assert.Equal(IssueSeverity.Info, issue.Severity);
    }

    [Fact]
    public void Health_unreadable_labeled_cvs_need_caution() =>
        Assert.Equal(SetHealthLevel.Caution, EvaluationSetHealth.Assess(50, 25, 2, failedExtractionCount: 2).Level);

    [Fact]
    public void Health_unknown_job_count_skips_job_rule() =>
        Assert.Empty(EvaluationSetHealth.Assess(50, 25).Issues);

    [Fact]
    public void Health_clamps_inconsistent_counts()
    {
        var h = EvaluationSetHealth.Assess(10, 25);
        Assert.Equal(10, h.Pass);
        Assert.Equal(0, h.Fail);
        Assert.Equal(SetHealthLevel.Poor, h.Level);
    }

    [Theory]
    [InlineData(30, 13.9)]
    [InlineData(50, 10.9)]
    [InlineData(100, 7.8)]
    public void Health_margin_of_error_shrinks_with_size(int n, double expected) =>
        Assert.Equal((decimal)expected, EvaluationSetHealth.MarginOfError(n));

    // ---------------- Trang /evaluation-sets ----------------

    [Fact]
    public async Task Set_page_hosts_one_manager_health_and_runs_with_revision_warning()
    {
        var api = new Api();
        var nav = new Nav("https://app.test/evaluation-sets?set=7");
        await using var services = Services(api, nav);
        await using var ui = new Ui(services);
        await ui.Show<SetsPage>();

        var html = await ui.Until(t => t.Contains("Lượt Prompt Test trên bộ này") && t.Contains("#41") && t.Contains("Hồ sơ #11"));
        Assert.Equal(1, Count(html, "evaluation-set-manager"));                 // không render hai EvaluationSetManager
        Assert.Contains("Cách xây một bộ đánh giá đáng tin cậy", html);
        Assert.Contains("Dùng được, cần thận trọng", html);                      // 40 CV có nhãn → tạm đủ
        Assert.Contains("Tạm đủ: 40 CV có nhãn", html);
        Assert.Contains("Sai số tỷ lệ đúng", html);
        Assert.Contains("set-card verdict-caution", html);                      // thẻ bộ 40 nhãn: viền vàng + chữ kết luận
        Assert.Contains("set-card verdict-empty", html);                        // bộ chưa có nhãn
        Assert.Contains("Chưa gán nhãn", html);
        Assert.Contains("Bộ đã đổi (hiện tại 3)", html);                         // lượt #40 chạy trên phiên bản 2
        Assert.Contains("Bộ đã thay đổi", html);
        Assert.Contains("/prompts?set=7#prompt-test-panel", html);              // Bước 5 chỉ ở trang System Prompt
        Assert.DoesNotContain("Chạy đánh giá", html);                            // không có PromptTestPanel ở đây
        Assert.DoesNotContain("Sửa bộ", html);                                   // không có API sửa / sao chép bộ
        Assert.DoesNotContain("Sao chép", html);
        Assert.Empty(api.Writes);
        Assert.Contains("GET /api/prompt-tests?evaluationSetId=7", api.Calls);
    }

    [Fact]
    public async Task Selecting_another_set_updates_url_and_loads_its_runs()
    {
        var api = new Api();
        var nav = new Nav("https://app.test/evaluation-sets?set=7");
        await using var services = Services(api, nav);
        await using var ui = new Ui(services);
        await ui.Show<SetsPage>();
        await ui.Until(t => t.Contains("#41"));

        await ui.ClickByText("button", "Bộ dự phòng");
        await ui.Until(t => t.Contains("Bộ này chưa có lượt Prompt Test nào"));
        Assert.EndsWith("/evaluation-sets?set=8", nav.Uri);
        Assert.True(nav.LastReplace);                                           // không thêm bước Back
        Assert.Contains("GET /api/prompt-tests?evaluationSetId=8", api.Calls);
        Assert.Empty(api.Writes);
    }

    [Fact]
    public async Task Failed_run_load_shows_retry_without_writing()
    {
        var api = new Api { FailRuns = true };
        var nav = new Nav("https://app.test/evaluation-sets?set=7");
        await using var services = Services(api, nav);
        await using var ui = new Ui(services);
        await ui.Show<SetsPage>();

        var html = await ui.Until(t => t.Contains("Không tải được lượt Prompt Test của bộ này"));
        Assert.Contains("Thử lại", html);
        Assert.DoesNotContain("Lỗi máy chủ nội bộ chi tiết", html);              // không đưa raw lỗi API ra giao diện
        api.FailRuns = false;
        await ui.ClickByText("button", "Thử lại");
        await ui.Until(t => t.Contains("#41"));
        Assert.Empty(api.Writes);
    }

    [Fact]
    public async Task Unsaved_label_blocks_leaving_page_until_discarded()
    {
        var api = new Api();
        var nav = new Nav("https://app.test/evaluation-sets?set=7");
        await using var services = Services(api, nav);
        await using var ui = new Ui(services);
        await ui.Show<SetsPage>();
        await ui.Until(t => t.Contains("Hồ sơ #11"));

        await ui.ChangeByLabel("Đạt – hồ sơ 11");                                 // hồ sơ 11 đang Không đạt → bản nháp, chưa lưu
        await ui.Until(t => t.Contains("1 thay đổi chưa lưu"));
        await ui.Navigate(nav, "/prompts");
        var html = await ui.Until(t => t.Contains("Bạn có nhãn / ghi chú HR chưa lưu"));
        Assert.EndsWith("/evaluation-sets?set=7", nav.Uri);                     // vẫn ở trang
        Assert.Contains("1 thay đổi chưa lưu", html);                           // bản nháp còn nguyên

        await ui.Click("keep-draft");
        await ui.Until(t => !t.Contains("Bạn có nhãn / ghi chú HR chưa lưu"));
        await ui.Navigate(nav, "/prompts");
        await ui.Until(t => t.Contains("Bạn có nhãn / ghi chú HR chưa lưu"));
        await ui.Click("discard-draft");
        Assert.EndsWith("/prompts", nav.Uri);
        Assert.Empty(api.Writes);                                               // bỏ bản nháp không gửi lệnh ghi nào
    }

    // ---------------- Trang /prompts sau B25 ----------------

    [Fact]
    public async Task Prompts_page_no_longer_hosts_set_manager_and_preselects_set_from_query()
    {
        var api = new Api();
        var nav = new Nav("https://app.test/prompts?set=7");
        await using var services = Services(api, nav);
        await using var ui = new Ui(services);
        await ui.Show<PromptsPage>();

        var html = await ui.Until(t => t.Contains("Quản lý bộ đánh giá") && t.Contains("Bộ B25 chính (40 CV có nhãn)"));
        Assert.Equal(0, Count(html, "evaluation-set-manager"));
        Assert.Contains("/evaluation-sets?set=7", html);
        Assert.Equal(1, Count(html, " id=\"prompt-test-panel\""));            // một chỗ duy nhất để chạy Prompt Test
        Assert.Empty(api.Writes);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("GET /api/evaluation-sets/7", StringComparison.Ordinal)); // không tải thành viên bộ ở trang này
    }

    // ---------------- Hạ tầng test ----------------

    private static int Count(string text, string value)
    {
        var n = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static ServiceProvider Services(Api api, Nav nav)
    {
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        var anonymity = new AnonymityState();
        anonymity.SetAsync(false).GetAwaiter().GetResult();
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<NavigationManager>(nav)
            .AddSingleton<IJSRuntime>(new Js())
            .AddSingleton(anonymity)
            .AddSingleton(new PromptApiClient(http, NullLogger<PromptApiClient>.Instance))
            .AddSingleton(new AiPreviewApiClient(http, NullLogger<AiPreviewApiClient>.Instance));
        services.AddSupplyValueFromQueryProvider();
        return services.AddFixtureFileDownloads(http).BuildServiceProvider();
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value, value.GetType()) };

    private sealed class Api : HttpMessageHandler
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<string> Writes { get; } = new();
        public bool FailRuns { get; set; }

        private static readonly List<EvaluationSetDto> Sets =
        [
            new() { EvaluationSetId = 7, Name = "Bộ B25 chính", LabelCount = 40, PassCount = 20, MemberCount = 40, Revision = 3, RunCount = 2 },
            new() { EvaluationSetId = 8, Name = "Bộ dự phòng", LabelCount = 0, PassCount = 0, MemberCount = 0, Revision = 1 }
        ];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            Calls.Enqueue($"{request.Method} {request.RequestUri.PathAndQuery}");
            if (request.Method != HttpMethod.Get) { Writes.Enqueue($"{request.Method} {path}"); return Task.FromResult(Json(new { }, HttpStatusCode.MethodNotAllowed)); }
            var query = request.RequestUri.Query;
            return Task.FromResult(path switch
            {
                "/api/evaluation-sets" => Json(Sets),
                "/api/evaluation-sets/7" => Json(Detail(7, "Bộ B25 chính", 3, Members(40))),
                "/api/evaluation-sets/8" => Json(Detail(8, "Bộ dự phòng", 1, [])),
                "/api/prompt-tests" when query.Contains("evaluationSetId=7") => FailRuns
                    ? Json(new { title = "Lỗi máy chủ nội bộ chi tiết" }, HttpStatusCode.InternalServerError)
                    : Json(new List<PromptTestRunDto> { Run(41, 3, 3, 82.5m), Run(40, 2, 3, 77.5m) }),
                "/api/prompt-tests" when query.Contains("evaluationSetId=8") => Json(new List<PromptTestRunDto>()),
                "/api/prompt-tests" => Json(new List<PromptTestRunDto>()),
                "/api/prompts" => Json(new List<PromptVersionDto>
                {
                    new() { PromptVersionId = 1, VersionName = "v1", SystemPrompt = "Quy tắc chấm", IsActive = true, CreatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) }
                }),
                _ => Json(new { detail = "Không thuộc B25 test." }, HttpStatusCode.NotFound)
            });
        }

        private static List<EvaluationSetMemberDto> Members(int count) => Enumerable.Range(11, count).Select(id => new EvaluationSetMemberDto
        {
            ApplicationId = id, CvId = Guid.NewGuid(), CandidateName = $"Ứng viên {id}", FileName = $"cv-{id}.pdf",
            JobId = id % 2 == 0 ? 1 : 2, JobTitle = id % 2 == 0 ? "Backend" : "Frontend", Stage = "New",
            ExtractionStatus = "Completed", HasExtractedText = true, IsReady = true, IsPass = id % 2 == 0,
            AddedBy = "HR", AddedAt = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc)
        }).ToList();

        private static EvaluationSetDetailDto Detail(int id, string name, int revision, List<EvaluationSetMemberDto> members) => new()
        {
            EvaluationSetId = id, Name = name, Revision = revision, Members = members,
            LabelCount = members.Count, PassCount = members.Count(m => m.IsPass == true),
            Readiness = new()
            {
                MemberCount = members.Count, LabeledCount = members.Count, PassCount = members.Count(m => m.IsPass == true),
                FailCount = members.Count(m => m.IsPass == false), CanRun = members.Count > 0
            }
        };

        private static PromptTestRunDto Run(int id, int revision, int current, decimal accuracy) => new()
        {
            RunId = id, PromptVersionId = 1, PromptVersionName = "v1", EvaluationSetId = 7, EvaluationSetName = "Bộ B25 chính",
            ModelName = "gemini-test", Status = "Completed", TotalItems = 40, ProcessedItems = 40,
            CreatedAt = new DateTime(2026, 11, 10 + id - 40, 2, 0, 0, DateTimeKind.Utc), AccuracyPercent = accuracy, TotalLabeledCount = 40,
            EvaluationSetRevision = revision, CurrentSetRevision = current
        };
    }

    private sealed class Nav : NavigationManager
    {
        public bool LastReplace { get; private set; }
        public Nav(string uri) => Initialize("https://app.test/", uri);
        protected override void SetNavigationLockState(bool value) { }
        protected override void HandleLocationChangingHandlerException(Exception exception, LocationChangingContext context) =>
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            LastReplace = options.ReplaceHistoryEntry;
            var target = ToAbsoluteUri(uri).AbsoluteUri;
            // Như trình duyệt: hỏi các NavigationLock trước, chỉ đổi địa chỉ khi không bị chặn.
            var allowed = NotifyLocationChangingAsync(target, options.HistoryEntryState, isNavigationIntercepted: false);
            if (!allowed.IsCompleted) throw new InvalidOperationException("Location changing handler must complete synchronously in this test.");
            if (!allowed.Result) return;
            Uri = target;
            NotifyLocationChanged(false);
        }
    }

    private sealed class Js : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) =>
            ValueTask.FromResult(this is TValue self ? self : default(TValue)!);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Ui(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        private int root = -1;
        protected override RendererInfo RendererInfo { get; } = new("B25-test", isInteractive: true);
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception error) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);

        public Task Show<T>() where T : IComponent => Dispatcher.InvokeAsync(async () =>
        { root = AssignRootComponentId(InstantiateComponent(typeof(T))); await RenderRootComponentAsync(root, ParameterView.Empty); });
        public Task Navigate(Nav nav, string uri) => Dispatcher.InvokeAsync(() => nav.NavigateTo(uri));
        public Task Click(string id) => Fire((f, a) => a.Any(x => x.AttributeName == "id" && x.AttributeValue?.ToString() == id), "onclick", new MouseEventArgs(), id);
        public Task ChangeByLabel(string label) => Fire((f, a) => a.Any(x => x.AttributeName == "aria-label" && x.AttributeValue?.ToString() == label), "onchange", new ChangeEventArgs { Value = "on" }, label);
        public Task ClickByText(string element, string text) => Dispatcher.InvokeAsync(() =>
        {
            foreach (var frames in Trees(root))
                for (var i = 0; i < frames.Length; i++)
                {
                    if (frames[i].FrameType != RenderTreeFrameType.Element || frames[i].ElementName != element) continue;
                    var end = i + frames[i].ElementSubtreeLength;
                    var inner = string.Concat(frames.Skip(i).Take(end - i).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                        .Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
                    if (!inner.Contains(text, StringComparison.Ordinal)) continue;
                    var attributes = frames.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
                    var handler = attributes.FirstOrDefault(a => a.AttributeName == "onclick");
                    if (handler.AttributeEventHandlerId == 0) continue;
                    return DispatchEventAsync(handler.AttributeEventHandlerId, null, new MouseEventArgs());
                }
            throw new InvalidOperationException($"Missing {element}: {text}");
        });

        public Task<string> All() => Dispatcher.InvokeAsync(() => string.Concat(Trees(root).SelectMany(f => f).Select(f => f.FrameType switch
        {
            RenderTreeFrameType.Text => f.TextContent,
            RenderTreeFrameType.Markup => f.MarkupContent,
            RenderTreeFrameType.Attribute when f.AttributeValue is string s => $" {f.AttributeName}=\"{s}\"",
            _ => null
        })));

        public async Task<string> Until(Func<string, bool> predicate)
        {
            var time = Stopwatch.StartNew();
            while (true)
            {
                var text = await All();
                if (predicate(text)) return text;
                if (time.ElapsedMilliseconds > 10000) throw new TimeoutException(text);
                await Task.Delay(10);
            }
        }

        private Task Fire(Func<RenderTreeFrame[], RenderTreeFrame[], bool> match, string name, EventArgs args, string what) => Dispatcher.InvokeAsync(() =>
        {
            foreach (var frames in Trees(root))
                for (var i = 0; i < frames.Length; i++)
                {
                    if (frames[i].FrameType != RenderTreeFrameType.Element) continue;
                    var attributes = frames.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
                    if (!match(frames, attributes)) continue;
                    if (attributes.Any(a => a.AttributeName == "disabled" && a.AttributeValue is true)) return Task.CompletedTask;
                    return DispatchEventAsync(attributes.Single(a => a.AttributeName == name).AttributeEventHandlerId, null, args);
                }
            throw new InvalidOperationException("Missing control: " + what);
        });

        private IEnumerable<RenderTreeFrame[]> Trees(int id)
        {
            if (id < 0) yield break;
            var frames = GetCurrentRenderTreeFrames(id); var copy = frames.Array.Take(frames.Count).ToArray(); yield return copy;
            foreach (var child in copy.Where(f => f.FrameType == RenderTreeFrameType.Component)) foreach (var nested in Trees(child.ComponentId)) yield return nested;
        }
    }
}
#pragma warning restore BL0006
