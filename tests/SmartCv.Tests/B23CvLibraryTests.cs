extern alias web;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Runs;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
using LibraryPage = web::SmartCv.Web.Components.Pages.CvLibrary;
using AttachDialog = web::SmartCv.Web.Components.CvLibrary.AttachCandidateDialog;
using web::SmartCv.Web.Components.Layout;

namespace SmartCv.Tests.B23Ui;

#pragma warning disable BL0006 // Exercise real component events, following the existing B19/B20/B26 test harness.
public class B23CvLibraryTests
{
    [Theory]
    [InlineData("", true)] [InlineData("?search=a", false)] [InlineData("?search=React", true)]
    [InlineData("?page=0", false)] [InlineData("?page=-1", false)] [InlineData("?page=abc", false)]
    [InlineData("?page=2147483647", false)] [InlineData("?pageSize=0", false)] [InlineData("?pageSize=101", false)]
    [InlineData("?jobId=0", false)] [InlineData("?stage=Hired", false)] [InlineData("?stage=Rejected", true)]
    [InlineData("?source=Email", true)] [InlineData("?status=Anonymized", true)] [InlineData("?status=completed", false)]
    [InlineData("?search=React&SEARCH=SQL", false)] [InlineData("?page=1&page=2", false)]
    public void URL_validation_matches_A23(string query, bool valid)
    {
        var (filter, error) = CvLibraryFilter.FromUri("https://app.test/cvs" + query);
        Assert.Equal(valid, filter is not null); Assert.Equal(!valid, error is not null);
    }

    [Fact]
    public void URLs_round_trip_whitespace_unicode_special_characters_all_filters_and_page()
    {
        var filter = new CvLibraryFilter("  Nguyễn\t  C# & SQL  ", 2, "Rejected", "Email", "Completed", 3);
        var (valid, error) = filter.Validate(); Assert.Null(error); Assert.Equal("Nguyễn C# & SQL", valid!.Search);
        Assert.EndsWith("page=3&pageSize=20", valid.ApiQuery);
        Assert.Contains("search=Nguy%E1%BB%85n%20C%23%20%26%20SQL", valid.ApiQuery);
        Assert.Equal(valid, CvLibraryFilter.FromUri("https://app.test" + valid.Url).Filter);
        // B24: Hồ sơ CV nằm trong trang Ứng viên; /cvs cũ chuyển hướng về đây và giữ bộ lọc.
        Assert.Equal("/candidates?view=cvs", new CvLibraryFilter().Url);
        Assert.Equal("/candidates?view=cvs&stage=Rejected&page=3", CandidatesView.LegacyCvsTarget("https://app.test/cvs?stage=Rejected&page=3"));
        Assert.Null(CvLibraryFilter.FromUri("https://app.test/cvs?search=" + new string('a', 101)).Filter);
    }

    [Fact]
    public void Privacy_retention_and_unknown_values_are_explicit()
    {
        var cv = Row(); var now = new DateTime(2026, 11, 19, 3, 0, 0, DateTimeKind.Utc);
        Assert.Contains("Ứng viên · CV", CvLibraryText.Candidate(cv, true)); Assert.Equal("CV đã ẩn tên", CvLibraryText.File(cv, true));
        Assert.Equal("PRIVATE NAME", CvLibraryText.Candidate(cv, false)); Assert.Equal("—", CvLibraryText.Stage(null));
        Assert.Null(CvLibraryText.Reason("PRIVATE REASON", false)); Assert.Null(CvLibraryText.Reason("MissingSkills", true));
        Assert.Equal("Thiếu kỹ năng bắt buộc", CvLibraryText.Reason("MissingSkills", false));
        Assert.Equal("—", CvLibraryText.Retention(null, null, now));
        Assert.Contains("còn 1 ngày", CvLibraryText.Retention(now.Date.AddHours(18), null, now));
        Assert.Contains("quá hạn 2 ngày", CvLibraryText.Retention(now.AddDays(-2), null, now));
        Assert.Contains("hết hạn hôm nay", CvLibraryText.Retention(now, null, now));
        Assert.Equal("Đã ẩn danh", CvLibraryText.Retention(null, now, now));
        Assert.True(CvLibraryText.RetentionWarning(new() { RetainUntil = now.AddDays(14) }, now));
        Assert.False(CvLibraryText.RetentionWarning(new() { RetainUntil = now.AddDays(15) }, now));
    }

    [Fact]
    public async Task Client_never_sends_invalid_queries_and_does_not_log_sensitive_search_or_exception_text()
    {
        var api = new Api { List = (_, _) => Task.FromResult(Json(new { detail = "PRIVATE sql path C:/CV" }, 500)) };
        var log = new RecordingLogger<CvApiClient>(); var client = Client(api, log);
        foreach (var search in new[] { "a", new string('x', 101) }) Assert.False((await client.ListAsync(new(search))).IsSuccess);
        Assert.Empty(api.Calls);
        Assert.False((await client.ListAsync(new("PRIVATE SECRET"))).IsSuccess);
        Assert.DoesNotContain("PRIVATE", string.Join(" ", log.Messages));
        api.List = (_, _) => throw new HttpRequestException("PRIVATE SECRET connection");
        Assert.False((await client.ListAsync(new("PRIVATE SECRET"))).IsSuccess);
        Assert.DoesNotContain("PRIVATE", string.Join(" ", log.Messages));
    }

    [Fact]
    public async Task Debounce_runs_only_last_action_and_cancel_dispose_prevent_actions()
    {
        using var debounce = new Debouncer(TimeSpan.FromMilliseconds(50));
        Assert.Equal(400, new Debouncer().Delay.TotalMilliseconds);
        var actions = new List<string>();
        var first = debounce.Trigger(() => { actions.Add("first"); return Task.CompletedTask; });
        var last = debounce.Trigger(() => { actions.Add("last"); return Task.CompletedTask; });
        await Task.WhenAll(first, last); Assert.Equal(["last"], actions);
        var pending = debounce.Trigger(() => { actions.Add("cancelled"); return Task.CompletedTask; });
        debounce.Cancel(); await pending; Assert.Single(actions);
        pending = debounce.Trigger(() => { actions.Add("disposed"); return Task.CompletedTask; });
        debounce.Dispose(); await pending; Assert.Single(actions);
    }

    [Fact]
    public async Task Old_uncancellable_request_cannot_overwrite_new_filter_or_invalid_input()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Api { List = (r, _) => r.RequestUri!.Query.Contains("search=old") ? old.Task : Task.FromResult(Json(Page(1, 1, Row()))) };
        using var workspace = new CvLibraryWorkspace(Client(api));
        var pending = workspace.LoadAsync(new("old"));
        await workspace.LoadAsync(new("new"));
        old.SetResult(Json(Page(1, 99))); await pending;
        Assert.Equal("new", workspace.Filter!.Search); Assert.Equal(1, workspace.Data!.TotalCount);
        await workspace.LoadAsync(new("a")); Assert.Null(workspace.Data); Assert.NotNull(workspace.Error);
    }

    [Fact]
    public async Task Workspace_rejects_mismatched_page_and_handles_timeout_and_disposal()
    {
        var api = new Api { List = (_, _) => Task.FromResult(Json(Page(2, 1))) };
        using var workspace = new CvLibraryWorkspace(Client(api)); await workspace.LoadAsync(new());
        Assert.Contains("không khớp", workspace.Error); Assert.Null(workspace.Data);
        api.List = (_, _) => throw new TaskCanceledException(); await workspace.LoadAsync(new());
        Assert.Contains("thời gian", workspace.Error); Assert.False(workspace.Loading);
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.List = (_, _) => gate.Task; var pending = workspace.LoadAsync(new()); workspace.Dispose();
        gate.SetResult(Json(Page(1, 1, Row()))); await pending; Assert.Null(workspace.Data);
    }

    [Fact]
    public async Task Page_reads_real_A23_pipeline_with_screening_scores_and_safe_anonymized_rows()
    {
        using var factory = RunHistoryEndpointTests.Factory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>(); await db.Database.EnsureCreatedAsync();
            await A23CvListEndpointTests.SeedAsync(db);
        }
        using var sp = Services(factory.Server.CreateHandler(), new Nav(), Normal());
        var html = WebUtility.HtmlDecode(await Html<LibraryPage>(sp));
        Assert.Contains("Nguyễn Văn An", html); Assert.Contains("cv-nguyen-van-an.pdf", html); Assert.Contains(">82<", html);
        Assert.DoesNotContain(">95<", html); Assert.DoesNotContain("PRIVATE-legacy", html); Assert.Contains("Hồ sơ đã ẩn danh", html);
        // B24: tên ứng viên đã gắn mở hồ sơ người (kèm from=cvs để quay lại đúng chế độ); hồ sơ đã ẩn danh không có liên kết.
        Assert.Contains("Chưa chấm", html); Assert.Contains("+1 vị trí", html); Assert.Contains("?from=cvs", html);
        Assert.DoesNotContain("PRIVATE-legacy", html);
    }

    [Fact]
    public async Task Anonymous_HTML_has_no_personal_values_in_text_title_aria_hidden_fields_or_file_links()
    {
        var api = new Api { List = (_, _) => Task.FromResult(Json(Page(1, 1, Row()))) };
        using var sp = Services(api, new Nav()); var html = WebUtility.HtmlDecode(await Html<LibraryPage>(sp));
        Assert.DoesNotContain("PRIVATE", html); Assert.Contains("CV đã ẩn tên", html); Assert.DoesNotContain("/file\"", html);
        Assert.All(api.Methods, method => Assert.Equal("GET", method));
    }

    [Fact]
    public async Task Permanent_anonymization_overrides_normal_view_even_with_dirty_legacy_DTO()
    {
        var api = new Api { List = (_, _) => Task.FromResult(Json(Page(1, 1, Row(anonymized: true)))) };
        using var sp = Services(api, new Nav(), Normal()); var html = WebUtility.HtmlDecode(await Html<LibraryPage>(sp));
        Assert.DoesNotContain("PRIVATE", html); Assert.Contains("Hồ sơ đã ẩn danh", html);
        Assert.DoesNotContain("id=\"attach-", html); Assert.DoesNotContain("/file\"", html);
    }

    [Fact]
    public async Task Null_score_remains_unscored_while_zero_is_a_real_score_and_names_are_not_truncated_in_HTML()
    {
        var first = Row(); var unknown = new CvListItemDto { CvId = Guid.NewGuid(), FileName = "long-" + new string('x', 180) + ".pdf" };
        var api = new Api { List = (_, _) => Task.FromResult(Json(Page(1, 2, first, unknown))) };
        using var sp = Services(api, new Nav(), Normal()); var html = WebUtility.HtmlDecode(await Html<LibraryPage>(sp));
        Assert.Contains("Chưa chấm", html); Assert.Contains(">0</strong>", html); Assert.Contains(unknown.FileName, html);
        Assert.Contains("QA Engineer", html); Assert.Contains("Loại", html); Assert.Contains("Đang xem", html);
        Assert.Equal("—", CvLibraryText.Retention(DateTime.MaxValue, null, Now));
    }

    [Fact]
    public async Task Menu_adds_library_and_keeps_all_existing_destinations()
    {
        using var sp = Services(new Api(), new Nav()); var html = await Html<NavMenu>(sp);
        // B24: một mục "Ứng viên" thay cho "Kho CV" (không để hai mục chồng chức năng).
        foreach (var route in new[] { "candidates", "upload", "results", "prompts", "jobs", "runs" }) Assert.Contains($"href=\"{route}\"", html);
        Assert.DoesNotContain("href=\"cvs\"", html); Assert.DoesNotContain("Kho CV", html);
    }

    [Theory]
    [InlineData("", 0, "Kho CV đang trống")]
    [InlineData("?search=React", 0, "Không có hồ sơ phù hợp")]
    [InlineData("?page=9", 5, "Trang này không còn hồ sơ")]
    [InlineData("?page=9", 0, "Trang này không còn hồ sơ")]
    public async Task Empty_filtered_empty_and_out_of_range_are_distinct(string query, int total, string expected)
    {
        var api = new Api { List = (_, _) => Task.FromResult(Json(Page(query.Contains("page=9") ? 9 : 1, total))) };
        using var sp = Services(api, new Nav("https://app.test/cvs" + query));
        Assert.Contains(expected, WebUtility.HtmlDecode(await Html<LibraryPage>(sp)));
    }

    [Theory]
    [InlineData(400)] [InlineData(404)] [InlineData(500)] [InlineData(503)]
    public async Task API_errors_are_safe_and_retry_works(int status)
    {
        var api = new Api { List = (_, _) => Task.FromResult(Json(new { detail = "PRIVATE connection string SQL path" }, status)) };
        using var sp = Services(api, new Nav()); await using var ui = new Ui(sp); await ui.Show<LibraryPage>();
        Assert.DoesNotContain("PRIVATE", await ui.All()); Assert.Contains("Chưa tải được", await ui.All());
        api.List = (_, _) => Task.FromResult(Json(Page(1, 1, Row()))); await ui.Click("cvs-retry");
        await ui.Until(s => s.Contains("CV đã ẩn tên"));
    }

    [Fact]
    public async Task Real_events_debounce_search_validate_length_clear_and_combine_filters_on_server()
    {
        var api = new Api(); using var sp = Services(api, new Nav()); await using var ui = new Ui(sp); await ui.Show<LibraryPage>();
        api.Calls.Clear();
        var tasks = new List<Task>();
        foreach (var text in new[] { "R", "Re", "Rea", "Reac", "React" }) tasks.Add(ui.Input("cvs-search", text));
        await Task.WhenAll(tasks); await ui.Until(_ => api.Calls.Count > 0);
        Assert.Single(api.Calls); Assert.Contains("search=React", api.Calls.Single());
        api.Calls.Clear(); await ui.Input("cvs-search", "x"); await ui.Input("cvs-search", new string('x', 101));
        Assert.Empty(api.Calls); Assert.Contains("2 đến 100", await ui.All());
        await ui.Click("cvs-clear-search"); await ui.Until(_ => api.Calls.Any()); Assert.DoesNotContain("search=", api.Calls.Last());
        await ui.Change("cvs-job", "2"); await ui.Change("cvs-stage", "Rejected"); await ui.Change("cvs-source", "Email"); await ui.Change("cvs-status", "Completed");
        await ui.Until(_ => api.Calls.Any(c => c.Contains("status=Completed")));
        Assert.Contains("jobId=2&stage=Rejected&source=Email&status=Completed&page=1&pageSize=20", api.Calls.Last());
        Assert.Contains("activeOnly=false", api.AllCalls.First(c => c.Contains("api/jobs")));
        Assert.All(api.Methods, m => Assert.Equal("GET", m));
    }

    [Fact]
    public async Task Paging_URL_refresh_Back_Forward_and_reset_are_synchronized()
    {
        var api = new Api { List = (r, _) => { var page = int.Parse(QueryHelpers.ParseQuery(r.RequestUri!.Query)["page"]!); return Task.FromResult(Json(Page(page, 47, Row()))); } };
        var nav = new Nav(); using var sp = Services(api, nav); await using var ui = new Ui(sp); await ui.Show<LibraryPage>();
        await ui.Click("cvs-next"); await ui.Until(s => s.Contains("Trang 2/3")); Assert.Contains("page=2", nav.Uri);
        await ui.Go(nav, "/cvs?stage=Rejected&page=3"); await ui.Until(s => s.Contains("Trang 3/3"));
        await ui.Change("cvs-source", "Referral"); await ui.Until(s => s.Contains("Trang 1/3")); Assert.DoesNotContain("page=3", nav.Uri);
        await ui.Go(nav, "/cvs?page=2"); await ui.Until(s => s.Contains("Trang 2/3"));
        await ui.Go(nav, "/cvs?stage=Rejected&page=3"); await ui.Until(s => s.Contains("Trang 3/3"));
        await ui.Click("cvs-refresh"); Assert.Contains("stage=Rejected&page=3&pageSize=20", api.Calls.Last());
        Assert.DoesNotContain(api.Calls, c => c.Contains("page=0"));
    }

    [Fact]
    public async Task Invalid_URL_never_requests_all_CVs_and_clear_recovers()
    {
        var api = new Api(); using var sp = Services(api, new Nav("https://app.test/cvs?page=0"));
        await using var ui = new Ui(sp); await ui.Show<LibraryPage>(); Assert.Empty(api.Calls);
        await ui.Click("cvs-clear"); await ui.Until(_ => api.Calls.Any()); Assert.Contains("page=1", api.Calls.Last());
    }

    [Fact]
    public async Task Dialog_validates_with_Core_requires_explicit_duplicate_choice_and_refresh_callback()
    {
        var api = new Api(); var attached = 0; using var sp = Services(api, new Nav(), Normal());
        await using var ui = new Ui(sp); await ui.Show<AttachDialog>(new() { ["CvId"] = Row().CvId, ["OnAttached"] = EventCallback.Factory.Create(this, () => attached++) });
        await ui.Input("attach-name", ""); await ui.Click("attach-save"); Assert.DoesNotContain("PUT", api.Methods);
        Assert.Contains("Họ tên là bắt buộc", await ui.All());
        await ui.Input("attach-name", "Người Thử"); await ui.Input("attach-email", "THU@example.com"); await ui.Click("attach-save");
        Assert.Contains("Có thể là ứng viên nộp lại", await ui.All()); Assert.Equal(0, attached);
        Assert.False(api.Bodies.Single().ConfirmNew);
        await ui.Click("match-7"); Assert.Equal(7, api.Bodies.Last().CandidateId); Assert.Equal(1, attached);
    }

    [Fact]
    public async Task Dialog_confirm_new_is_explicit()
    {
        var api = new Api(); using var sp = Services(api, new Nav(), Normal());
        await using var ui = new Ui(sp); await ui.Show<AttachDialog>(new() { ["CvId"] = Row().CvId });
        await ui.Click("attach-save"); await ui.Click("attach-confirm-new"); Assert.True(api.Bodies.Last().ConfirmNew);
    }

    [Fact]
    public async Task Dialog_existing_search_is_paged_and_debounced()
    {
        var api = new Api(); using var sp = Services(api, new Nav(), Normal());
        await using var ui = new Ui(sp); await ui.Show<AttachDialog>(new() { ["CvId"] = Row().CvId });
        await ui.Click("attach-existing"); api.AllCalls.Clear();
        var first = ui.Input("attach-search", "Ng"); var last = ui.Input("attach-search", "Người"); await Task.WhenAll(first, last);
        Assert.Single(api.AllCalls); Assert.Contains("search=Ng%C6%B0%E1%BB%9Di", api.AllCalls.Single());
        await ui.Click("candidate-next"); Assert.Contains("page=2&pageSize=10", api.AllCalls.Last());
        await ui.Click("choose-7"); Assert.Equal(7, api.Bodies.Last().CandidateId);
    }

    [Fact]
    public async Task Toggling_privacy_closes_dialog_and_removes_identity_from_every_rendered_attribute()
    {
        var api = new Api { List = (_, _) => Task.FromResult(Json(Page(1, 1, Row()))) };
        var anonymity = Normal(); using var sp = Services(api, new Nav(), anonymity); await using var ui = new Ui(sp); await ui.Show<LibraryPage>();
        await ui.Click("attach-" + CvLibraryText.ShortId(Row().CvId)); Assert.Contains("PRIVATE", await ui.All());
        await ui.Do(() => anonymity.SetAsync(true)); await ui.Until(s => s.Contains("CV đã ẩn tên") && !s.Contains("PRIVATE"));
        Assert.DoesNotContain("attach-email", await ui.All());
    }

    [Fact]
    public async Task Anonymous_dialog_never_fetches_or_renders_contact_suggestions()
    {
        var api = new Api(); using var sp = Services(api, new Nav()); var html = await HtmlWithParameters<AttachDialog>(sp, new() { ["CvId"] = Row().CvId });
        Assert.Empty(api.AllCalls); Assert.DoesNotContain("PRIVATE", html); Assert.DoesNotContain("attach-name", html);
    }

    private static readonly DateTime Now = new(2026, 11, 19, 3, 0, 0, DateTimeKind.Utc);
    private static CvListItemDto Row(bool anonymized = false) => new()
    {
        CvId = Guid.Parse("3f2a9c1b-0000-0000-0000-000000000000"), FileName = "PRIVATE-file.pdf", CandidateId = 7,
        CandidateName = "PRIVATE NAME", CandidateEmail = "PRIVATE@example.com", UploadedAt = Now, ExtractionStatus = "Completed", SourceChannel = "Email",
        AnonymizedAt = anonymized ? Now : null,
        Applications = [new() { JobId = 1, JobTitle = "Backend Developer", Stage = "Reviewing", LatestFinalScore = 0, LatestEvaluatedAt = Now }, new() { JobId = 2, JobTitle = "QA Engineer", Stage = "Rejected", RejectReason = "MissingSkills" }]
    };
    private static CvPageDto Page(int page, int total, params CvListItemDto[] rows) => new() { Page = page, PageSize = 20, TotalCount = total, TotalPages = (total + 19) / 20, Items = [.. rows] };
    private static HttpResponseMessage Json(object value, int status = 200) => new((HttpStatusCode)status) { Content = JsonContent.Create(value, value.GetType()) };
    private static AnonymityState Normal() { var state = new AnonymityState(); state.SetAsync(false).GetAwaiter().GetResult(); return state; }
    private static CvApiClient Client(HttpMessageHandler handler, ILogger<CvApiClient>? logger = null) => new(new(handler) { BaseAddress = new("http://localhost/") }, Options.Create(new CvUploadOptions()), logger ?? NullLogger<CvApiClient>.Instance);
    private static ServiceProvider Services(HttpMessageHandler handler, Nav nav, AnonymityState? anonymity = null) => new ServiceCollection().AddLogging()
        .AddSingleton<NavigationManager>(nav).AddSingleton(anonymity ?? new AnonymityState()).AddSingleton(Client(handler))
        .AddSingleton(new JobApiClient(new(handler) { BaseAddress = new("http://localhost/") }, NullLogger<JobApiClient>.Instance))
        .AddSingleton(new CandidateApiClient(new(handler) { BaseAddress = new("http://localhost/") }, NullLogger<CandidateApiClient>.Instance))
        .AddSingleton<IJSRuntime>(new Js()).AddB28Auth().BuildServiceProvider(); // B28: menu cần người đã đăng nhập
    private static async Task<string> Html<T>(ServiceProvider services) where T : IComponent
        => await HtmlWithParameters<T>(services, []);
    private static async Task<string> HtmlWithParameters<T>(ServiceProvider services, Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) => Messages.Add(format(state, error) + error);
    }
    private sealed class Api : HttpMessageHandler
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<string> AllCalls { get; } = new();
        public ConcurrentQueue<string> Methods { get; } = new();
        public List<AttachCandidateRequest> Bodies { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> List = (_, _) => Task.FromResult(Json(Page(1, 0)));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; AllCalls.Enqueue(request.RequestUri.PathAndQuery); Methods.Enqueue(request.Method.Method);
            if (path == "/api/jobs") return Json(new List<JobDto> { new() { JobId = 2, Title = "QA Engineer", IsActive = false } });
            if (path == "/api/cvs") { Calls.Enqueue(request.RequestUri.PathAndQuery); return await List(request, ct); }
            if (path.EndsWith("candidate-suggestion")) return Json(new CandidateSuggestionDto { FullName = "PRIVATE NAME", Email = "thu@example.com" });
            if (path == "/api/candidates") return Json(new CandidatePageDto { Page = int.Parse(QueryHelpers.ParseQuery(request.RequestUri.Query)["page"]!), PageSize = 10, TotalCount = 11, TotalPages = 2, Items = [new() { CandidateId = 7, FullName = "Người Thử", Email = "thu@example.com" }] });
            if (request.Method == HttpMethod.Put)
            {
                var body = (await request.Content!.ReadFromJsonAsync<AttachCandidateRequest>(ct))!; Bodies.Add(body);
                return Json(new AttachCandidateResponse { Outcome = body.CandidateId is not null || body.ConfirmNew ? "Attached" : "NeedsDecision", Matches = [new() { CandidateId = 7, FullName = "Người Thử", Email = "thu@example.com", MatchedOn = ["Email"] }] });
            }
            return Json(new { detail = "PRIVATE missing route" }, 404);
        }
    }
    private sealed class Nav : NavigationManager
    {
        public Nav(string uri = "https://app.test/cvs") => Initialize("https://app.test/", uri);
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigateToCore(uri, options.ForceLoad);
        public void Go(string uri) => NavigateToCore(uri, false);
    }
    private sealed class Js : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    private sealed class Ui(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        private int root = -1;
        protected override RendererInfo RendererInfo { get; } = new("B23-test", isInteractive: false);
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception error) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        public Task Show<T>(Dictionary<string, object?>? parameters = null) where T : IComponent => Dispatcher.InvokeAsync(async () =>
        { root = AssignRootComponentId(InstantiateComponent(typeof(T))); await RenderRootComponentAsync(root, ParameterView.FromDictionary(parameters ?? [])); });
        public Task Do(Func<Task> action) => Dispatcher.InvokeAsync(action);
        public Task Go(Nav nav, string uri) => Dispatcher.InvokeAsync(() => nav.Go(uri));
        public Task Click(string id) => Fire(id, "onclick", new MouseEventArgs());
        public Task Change(string id, string value) => Fire(id, "onchange", new ChangeEventArgs { Value = value });
        public Task Input(string id, string value) => Fire(id, "oninput", new ChangeEventArgs { Value = value });
        public Task<string> All() => Dispatcher.InvokeAsync(() => string.Concat(Trees(root).SelectMany(f => f).Select(f => f.FrameType switch
        { RenderTreeFrameType.Text => f.TextContent, RenderTreeFrameType.Markup => f.MarkupContent, RenderTreeFrameType.Attribute => f.AttributeValue as string, _ => null })));
        public async Task Until(Func<string, bool> predicate)
        {
            var time = Stopwatch.StartNew(); while (!predicate(await All())) { if (time.ElapsedMilliseconds > 10000) throw new TimeoutException(await All()); await Task.Delay(10); }
        }
        private Task Fire(string id, string name, EventArgs args) => Dispatcher.InvokeAsync(() =>
        {
            foreach (var frames in Trees(root))
            {
                for (var i = 0; i < frames.Length; i++)
                {
                    if (frames[i].FrameType != RenderTreeFrameType.Element) continue;
                    var attributes = frames.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
                    if (!attributes.Any(a => a.AttributeName == "id" && a.AttributeValue?.ToString() == id)) continue;
                    return DispatchEventAsync(attributes.Single(a => a.AttributeName == name).AttributeEventHandlerId, null, args);
                }
            }
            throw new InvalidOperationException("Missing control: " + id);
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
