extern alias web;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components.Pages;
using JobsPage = web::SmartCv.Web.Components.Pages.Jobs;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

#pragma warning disable BL0006 // Exercise actual component events with the built-in renderer.
public class B20JobsTests
{
    private static JobDto Job(int id = 1, bool active = true, bool expired = false, bool stats = true, int count = 6) => new()
    {
        JobId = id, Title = id == 1 ? "Backend Developer (.NET)" : "QA Engineer", IsActive = active, IsExpired = expired,
        Headcount = 3, ClosingDate = new(2026, 12, 31), HasEvaluations = stats ? false : null,
        ApplicationCount = stats ? count : null, StageCounts = stats ? ApplicationStages.All.ToDictionary(s => s, _ => count / 6) : null,
        MustHaveCount = 1, Skills = [new() { SkillCode = "NET", DisplayName = ".NET", SkillType = "MustHave" }]
    };

    [Theory]
    [InlineData(true, false, "Đang tuyển")]
    [InlineData(true, true, "Quá hạn")]
    [InlineData(false, true, "Ngừng tuyển")]
    [InlineData(false, false, "Ngừng tuyển")]
    public void Status_distinguishes_expiry_from_deactivation(bool active, bool expired, string text) => Assert.Equal(text, JobText.Status(Job(active: active, expired: expired)).Text);

    [Theory]
    [InlineData("New", "New")]
    [InlineData("reviewing", "Reviewing")]
    [InlineData("shortlisted", "Shortlisted")]
    [InlineData("INTERVIEW", "Interview")]
    [InlineData("Rejected", "Rejected")]
    [InlineData("OnHold", "OnHold")]
    [InlineData("flagged", null)]
    [InlineData("<script>", null)]
    [InlineData(null, null)]
    public void Stage_links_accept_only_six_canonical_values(string? input, string? expected)
    {
        Assert.Equal(expected, JobText.Stage(input));
        Assert.Equal("results?jobId=3" + (expected is null ? "" : "&filter=" + expected), JobText.ResultsUrl(3, input));
    }

    [Fact]
    public void Text_handles_missing_limits_and_explains_consequences()
    {
        Assert.Equal("Chưa xác định", JobText.Headcount(null)); Assert.Equal("3", JobText.Headcount(3));
        Assert.Equal("Không giới hạn", JobText.Closing(null)); Assert.Equal("31/12/2026", JobText.Closing(new(2026, 12, 31)));
        Assert.Contains("giữ nguyên", JobText.ConfirmText(Job()));
        Assert.Contains("đã qua", JobText.ConfirmText(Job(active: false, expired: true)));
    }

    private sealed class Server : HttpMessageHandler
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public List<JobDto> Jobs = [Job(), Job(2, false)];
        public int? ReadFailure, WriteFailure;
        public bool TimeoutAfterApply, Offline, WrongResponse;
        public TaskCompletionSource? Gate;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LastReadToken;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Enqueue(request.Method + " " + request.RequestUri!.PathAndQuery);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("{}", await request.Content!.ReadAsStringAsync(ct));
                if (Gate is not null) { Started.TrySetResult(); await Gate.Task.WaitAsync(ct); }
                if (WriteFailure is { } code) return Json(new { detail = "PRIVATE database error" }, code);
                var id = int.Parse(request.RequestUri.Segments[3].Trim('/'));
                var active = request.RequestUri.AbsolutePath.EndsWith("/activate");
                var updated = Job(WrongResponse ? 999 : id, active);
                Jobs = Jobs.Select(j => j.JobId == id ? updated : j).ToList();
                if (TimeoutAfterApply) throw new TaskCanceledException("Applied, but response lost");
                return Json(updated);
            }
            if (request.RequestUri.AbsolutePath.StartsWith("/api/jobs"))
            {
                if (Gate is not null)
                {
                    LastReadToken = ct;
                    Started.TrySetResult();
                    await Gate.Task.WaitAsync(ct);
                }
                if (Offline) throw new HttpRequestException("PRIVATE network error");
                if (ReadFailure is { } code) return Json(new { detail = "PRIVATE SQL error" }, code);
                return Json(Jobs);
            }
            if (request.RequestUri.AbsolutePath == "/api/runs/latest") return Json(new RunSummaryDto { RunId = 101, RunType = "Screening", Status = "Completed" });
            if (request.RequestUri.AbsolutePath == "/api/runs") return Json(new List<RunSummaryDto> { new() { RunId = 101, RunType = "Screening", Status = "Completed" } });
            if (request.RequestUri.AbsolutePath.EndsWith("/ranking")) return Json(new RankingResponse
            {
                RunId = 101, RunStatus = "Completed", JobId = 1, JobTitle = "Backend Developer", PromptVersionName = "test", FlaggedCount = 1,
                Jobs = [new() { JobId = 1, Title = "Backend Developer", Count = 6 }],
                Items = ApplicationStages.All.Select((s, i) => new RankingItemDto
                {
                    EvaluationId = i + 1, ApplicationId = i + 1, Rank = i + 1, CandidateName = "Candidate " + s, FileName = s + ".pdf", Stage = s,
                    Status = "Completed", FinalScore = 80, ScoreSource = "Combined", FitLabel = "Phù hợp",
                    ReviewFlags = i == 2 ? [new() { Code = "ScoreDisagreement", Message = "Test flag" }] : []
                }).ToList()
            });
            if (request.RequestUri.AbsolutePath == "/api/runs/101") return Json(new RunProgressDto(101, "Screening", "Completed", 1, "test", "test", .2m, .3m, .5m, 70, 6, 6, 0, 100, new(2026, 11, 18), new(2026, 11, 18)));
            return Json(new { }, 404);
        }
        private static HttpResponseMessage Json(object value, int code = 200) => new((HttpStatusCode)code) { Content = JsonContent.Create(value, value.GetType()) };
    }
    private static JobApiClient Api(Server server) => new(new HttpClient(server) { BaseAddress = new("https://api.test/") }, NullLogger<JobApiClient>.Instance);

    [Fact]
    public async Task List_reads_all_once_searches_names_and_filters_locally()
    {
        var server = new Server(); using var model = new JobsWorkspace(Api(server)); await model.LoadAsync();
        Assert.Equal("GET /api/jobs?activeOnly=false", Assert.Single(server.Calls));
        Assert.Single(model.Visible); model.Show = "all"; Assert.Equal(2, model.Visible.Count);
        model.Search = "  BACKEND  "; Assert.Equal(1, Assert.Single(model.Visible).JobId);
        model.Search = "not present"; Assert.Empty(model.Visible);
        model.ClearFilters(); model.Show = "inactive"; Assert.Equal(2, Assert.Single(model.Visible).JobId); Assert.Single(server.Calls);
    }

    [Fact]
    public async Task Confirmation_cancel_and_success_use_correct_POST_without_reloading_or_losing_other_jobs()
    {
        var server = new Server(); using var model = new JobsWorkspace(Api(server)); await model.LoadAsync();
        model.Ask(model.Jobs[0]); model.Cancel(); Assert.Null(model.Pending); Assert.Single(server.Calls);
        model.Ask(model.Jobs[0]); await model.ToggleAsync(); Assert.False(model.Jobs[0].IsActive); Assert.False(model.MessageIsError);
        model.Ask(model.Jobs[0]); await model.ToggleAsync(); Assert.True(model.Jobs[0].IsActive);
        Assert.Equal(2, model.Jobs.Count); Assert.False(model.Jobs[1].IsActive);
        Assert.Equal(new[] { "GET /api/jobs?activeOnly=false", "POST /api/jobs/1/deactivate", "POST /api/jobs/1/activate" }, server.Calls);
    }

    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(409)] [InlineData(500)] [InlineData(503)]
    public async Task Failed_write_locks_writes_until_successful_explicit_read(int code)
    {
        var server = new Server { WriteFailure = code }; using var model = new JobsWorkspace(Api(server)); await model.LoadAsync();
        model.Ask(model.Jobs[0]); await model.ToggleAsync();
        Assert.True(model.NeedsVerification); Assert.True(model.MessageIsError); Assert.DoesNotContain("PRIVATE", model.Message);
        model.Ask(model.Jobs[0]); await model.ToggleAsync(); Assert.Equal(2, server.Calls.Count);
        server.ReadFailure = 500; await model.LoadAsync(); Assert.True(model.NeedsVerification); Assert.DoesNotContain("PRIVATE", model.Error);
        server.ReadFailure = null; await model.LoadAsync(); Assert.False(model.NeedsVerification); Assert.True(model.CanToggle(model.Jobs[0]));
        Assert.Single(server.Calls, c => c.StartsWith("POST"));
    }

    [Fact]
    public async Task Timeout_after_server_applied_is_verified_by_GET_and_never_replayed()
    {
        var server = new Server { TimeoutAfterApply = true }; using var model = new JobsWorkspace(Api(server)); await model.LoadAsync();
        model.Ask(model.Jobs[0]); await model.ToggleAsync(); Assert.True(model.NeedsVerification); Assert.True(model.Jobs[0].IsActive);
        await model.ToggleAsync(); await model.LoadAsync(); Assert.False(model.Jobs[0].IsActive); Assert.False(model.NeedsVerification);
        Assert.Single(server.Calls, c => c.StartsWith("POST"));
    }

    [Fact]
    public async Task Missing_statistics_disable_write_and_preserve_null()
    {
        var server = new Server { Jobs = [Job(stats: false)] }; using var model = new JobsWorkspace(Api(server)); await model.LoadAsync();
        Assert.Null(model.Jobs[0].ApplicationCount); Assert.Null(model.Jobs[0].StageCounts); Assert.False(model.CanToggle(model.Jobs[0]));
        model.Ask(model.Jobs[0]); await model.ToggleAsync(); Assert.Single(server.Calls);
    }

    [Fact]
    public async Task Unexpected_success_response_requires_verification()
    {
        var server = new Server { WrongResponse = true }; using var model = new JobsWorkspace(Api(server)); await model.LoadAsync();
        model.Ask(model.Jobs[0]); await model.ToggleAsync(); Assert.True(model.NeedsVerification); Assert.Equal(1, model.Jobs[0].JobId);
    }

    [Fact]
    public async Task Duplicate_confirm_and_refresh_cannot_compete_with_inflight_write()
    {
        var server = new Server(); using var model = new JobsWorkspace(Api(server)); await model.LoadAsync();
        server.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); model.Ask(model.Jobs[0]); var pending = model.ToggleAsync(); await server.Started.Task;
        Assert.True(model.Busy); model.Cancel(); Assert.NotNull(model.Pending); await model.ToggleAsync(); await model.LoadAsync(); Assert.Equal(2, server.Calls.Count);
        server.Gate.SetResult(); await pending; Assert.False(model.Busy); Assert.False(model.Jobs[0].IsActive);
    }

    [Fact]
    public async Task Disposal_cancels_pending_read_and_ignores_late_completion()
    {
        var server = new Server { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var model = new JobsWorkspace(Api(server));
        var load = model.LoadAsync(); await server.Started.Task; model.Dispose(); await load;
        Assert.True(server.LastReadToken.IsCancellationRequested);
        Assert.Empty(model.Jobs); await model.LoadAsync(); Assert.Single(server.Calls);
    }

    [Fact]
    public async Task Offline_read_is_safe_and_manual_retry_recovers_empty_list()
    {
        var server = new Server { Offline = true }; using var model = new JobsWorkspace(Api(server)); await model.LoadAsync();
        Assert.NotNull(model.Error); Assert.DoesNotContain("PRIVATE", model.Error);
        server.Offline = false; server.Jobs = []; await model.LoadAsync(); Assert.Null(model.Error); Assert.Empty(model.Jobs);
    }

    [Fact]
    public async Task Actual_A21_HTTP_and_Web_client_toggle_roundtrip_updates_upload_picker_without_changing_job_data()
    {
        using var factory = global::SmartCv.Tests.Jobs.JobCrudEndpointTests.Factory();
        await global::SmartCv.Tests.Jobs.JobCrudEndpointTests.SeedAsync(factory);
        using var http = factory.CreateClient(); var api = new JobApiClient(http, NullLogger<JobApiClient>.Instance);
        using var model = new JobsWorkspace(api); await model.LoadAsync();
        var original = model.Jobs[0]; Assert.NotNull(original.StageCounts); Assert.Equal(6, original.StageCounts.Count);
        model.Ask(original); await model.ToggleAsync(); Assert.False(model.MessageIsError); Assert.False(model.Jobs[0].IsActive);
        var upload = await api.GetJobsAsync(); Assert.True(upload.IsSuccess); Assert.DoesNotContain(upload.Data!, j => j.JobId == original.JobId);
        await model.LoadAsync(); var stopped = model.Jobs.Single(j => j.JobId == original.JobId); Assert.False(stopped.IsActive);
        Assert.Equal(original.Title, stopped.Title); Assert.Equal(original.ApplicationCount, stopped.ApplicationCount);
        Assert.Equal(original.Skills.Select(s => s.SkillCode), stopped.Skills.Select(s => s.SkillCode));
        model.Ask(stopped); await model.ToggleAsync(); Assert.False(model.MessageIsError);
        upload = await api.GetJobsAsync(); Assert.Contains(upload.Data!, j => j.JobId == original.JobId);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", false)]
    public async Task Actual_A21_public_gate_does_not_offer_write_or_invent_statistics(string environment, bool enabled)
    {
        using var factory = global::SmartCv.Tests.Jobs.JobCrudEndpointTests.Factory(environment, enabled);
        await global::SmartCv.Tests.Jobs.JobCrudEndpointTests.SeedAsync(factory);
        using var http = factory.CreateClient(); using var model = new JobsWorkspace(new(http, NullLogger<JobApiClient>.Instance));
        await model.LoadAsync(); Assert.Null(model.Error); Assert.NotEmpty(model.Jobs);
        Assert.All(model.Jobs, job => { Assert.Null(job.ApplicationCount); Assert.Null(job.StageCounts); Assert.False(model.CanToggle(job)); });
    }

    private sealed class Nav : NavigationManager
    {
        public Nav(string uri = "https://app.test/jobs") { Initialize("https://app.test/", uri); }
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigateToCore(uri, options.ForceLoad);
    }
    private sealed class Js : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
    private static ServiceProvider Services(Server server, Nav nav)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(nav).AddSingleton(Api(server)).AddB28Auth(); // B28: Admin thấy đủ thao tác
        services.AddSingleton(new RunApiClient(new HttpClient(server) { BaseAddress = new("https://api.test/") }, NullLogger<RunApiClient>.Instance));
        services.AddSingleton(new ApplicationApiClient(new HttpClient(server) { BaseAddress = new("https://api.test/") }, NullLogger<ApplicationApiClient>.Instance));
        var anonymity = new AnonymityState(); anonymity.SetAsync(false).GetAwaiter().GetResult(); services.AddSingleton(anonymity); services.AddSingleton(new HrActorState()); services.AddSingleton<IJSRuntime>(new Js()); services.AddSupplyValueFromQueryProvider();
        return services.BuildServiceProvider();
    }
    private sealed class Events(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        protected override RendererInfo RendererInfo { get; } = new("B20-test", isInteractive: false);
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private int root;
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception error) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        public Task Show<T>() where T : IComponent => Dispatcher.InvokeAsync(async () => { root = AssignRootComponentId(InstantiateComponent(typeof(T))); await RenderRootComponentAsync(root); });
        private IEnumerable<RenderTreeFrame[]> Components(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id); var array = frames.Array.Take(frames.Count).ToArray(); yield return array;
            foreach (var f in array.Where(f => f.FrameType == RenderTreeFrameType.Component)) foreach (var child in Components(f.ComponentId)) yield return child;
        }
        private static string? Attr(RenderTreeFrame[] frames, int i, string name) => frames.Skip(i + 1).TakeWhile(a => a.FrameType == RenderTreeFrameType.Attribute).FirstOrDefault(a => a.AttributeName == name).AttributeValue?.ToString();
        public Task Click(string id) => Event(id, "onclick", new MouseEventArgs());
        public Task Event(string id, string eventName, EventArgs args) => Dispatcher.InvokeAsync(async () =>
        {
            foreach (var frames in Components(root)) for (var i = 0; i < frames.Length; i++) if (frames[i].FrameType == RenderTreeFrameType.Element && Attr(frames, i, "id") == id)
            {
                var handler = frames.Skip(i + 1).TakeWhile(a => a.FrameType == RenderTreeFrameType.Attribute).Single(a => a.AttributeName == eventName);
                await DispatchEventAsync(handler.AttributeEventHandlerId, null, args); return;
            }
            throw new InvalidOperationException("Missing element " + id);
        });
        public Task<string?> Attribute(string id, string name) => Dispatcher.InvokeAsync(() => Components(root).SelectMany(f => Enumerable.Range(0, f.Length).Where(i => f[i].FrameType == RenderTreeFrameType.Element && Attr(f, i, "id") == id).Select(i => Attr(f, i, name))).FirstOrDefault());
        public Task<string> Text() => Dispatcher.InvokeAsync(() => string.Join(" ", Components(root).SelectMany(f => f).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent)));
        public Task<string[]> Links(string css) => Dispatcher.InvokeAsync(() => Components(root).SelectMany(f => Enumerable.Range(0, f.Length).Where(i => f[i].FrameType == RenderTreeFrameType.Element && f[i].ElementName == "a" && Attr(f, i, "class") == css).Select(i => Attr(f, i, "href")!)).ToArray());
        public Task Navigate(Nav nav, string uri) => Dispatcher.InvokeAsync(() => nav.NavigateTo(uri));
    }

    [Fact]
    public async Task Page_search_filter_confirmation_cancel_and_toggle_are_wired_to_actual_events()
    {
        var server = new Server(); using var sp = Services(server, new()); await using var renderer = new Events(sp); await renderer.Show<JobsPage>();
        Assert.Contains("Backend Developer", await renderer.Text()); Assert.DoesNotContain("QA Engineer", await renderer.Text());
        await renderer.Click("jobs-filter-all"); Assert.Contains("QA Engineer", await renderer.Text());
        await renderer.Event("jobs-search", "oninput", new ChangeEventArgs { Value = "no match" }); Assert.Contains("Không tìm thấy vị trí phù hợp", await renderer.Text());
        await renderer.Click("jobs-clear"); await renderer.Click("job-toggle-1"); Assert.Contains("giữ nguyên", await renderer.Text());
        await renderer.Click("jobs-confirm-cancel"); Assert.Single(server.Calls);
        await renderer.Click("job-toggle-1"); await renderer.Click("jobs-confirm-submit"); Assert.Contains("Đã ngừng tuyển", await renderer.Text());
        Assert.Equal("POST /api/jobs/1/deactivate", server.Calls.Last());
    }

    [Theory]
    [InlineData(false, 0, "Chưa có thống kê hồ sơ")]
    [InlineData(true, 0, "0 hồ sơ")]
    [InlineData(true, 6, "6 hồ sơ")]
    public async Task Page_distinguishes_unavailable_statistics_and_zero(bool stats, int count, string expected)
    {
        var server = new Server { Jobs = [Job(stats: stats, count: count)] }; using var sp = Services(server, new()); await using var renderer = new Events(sp); await renderer.Show<JobsPage>();
        Assert.Contains(expected, await renderer.Text());
        if (!stats) { Assert.DoesNotContain("0 hồ sơ", await renderer.Text()); Assert.Equal("True", await renderer.Attribute("job-toggle-1", "disabled")); }
    }

    [Theory]
    [InlineData("New")] [InlineData("Reviewing")] [InlineData("Shortlisted")] [InlineData("Interview")] [InlineData("Rejected")] [InlineData("OnHold")]
    public async Task Results_stage_deep_links_filter_real_rows_and_survive_navigation(string stage)
    {
        var server = new Server(); var nav = new Nav("https://app.test/results?jobId=1&filter=" + stage); using var sp = Services(server, nav); await using var renderer = new Events(sp); await renderer.Show<Results>();
        Assert.Contains("GET /api/runs/latest?jobId=1", server.Calls); Assert.Equal("true", await renderer.Attribute("stage-filter-" + stage, "aria-pressed"));
        Assert.Contains("Candidate " + stage, await renderer.Text());
        Assert.Contains("/" + (ApplicationStages.All.ToList().IndexOf(stage) + 1) + "?", Assert.Single(await renderer.Links("name")));
        var count = server.Calls.Count; await renderer.Click("stage-filter-all"); Assert.DoesNotContain("filter=", nav.Uri); Assert.Equal(count, server.Calls.Count);
        await renderer.Navigate(nav, "/results?jobId=1&filter=" + stage); Assert.Equal("true", await renderer.Attribute("stage-filter-" + stage, "aria-pressed"));
        using var reloadSp = Services(server, new(nav.Uri)); await using var reload = new Events(reloadSp); await reload.Show<Results>(); Assert.Equal("true", await reload.Attribute("stage-filter-" + stage, "aria-pressed"));
    }

    [Fact]
    public async Task B19_flagged_filter_combines_with_stage_and_removal_preserves_the_other_filter()
    {
        var server = new Server(); var nav = new Nav("https://app.test/results?jobId=1&filter=flagged"); using var sp = Services(server, nav); await using var renderer = new Events(sp); await renderer.Show<Results>();
        Assert.Equal("true", await renderer.Attribute("flag-filter", "aria-pressed"));
        await renderer.Click("stage-filter-Shortlisted"); Assert.Contains("filter=flagged", nav.Uri); Assert.Contains("stage=Shortlisted", nav.Uri);
        await renderer.Click("flag-filter"); Assert.DoesNotContain("flagged", nav.Uri); Assert.Equal("true", await renderer.Attribute("stage-filter-Shortlisted", "aria-pressed"));
        await renderer.Click("flag-filter"); await renderer.Click("stage-filter-all"); Assert.Contains("filter=flagged", nav.Uri); Assert.DoesNotContain("stage=", nav.Uri);
    }
}
#pragma warning restore BL0006
