extern alias web;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
using web::SmartCv.Web.Components.Prompts;

namespace SmartCv.Tests;
public class PromptTestFlowTests
{
#pragma warning disable BL0006
    private sealed class LiveRenderer(IServiceProvider sp) : Renderer(sp, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", true);
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        private int root;
        public Task OpenAsync() => Dispatcher.InvokeAsync(async () =>
        {
            root = AssignRootComponentId(InstantiateComponent(typeof(PromptTestPanel)));
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?> { ["RunToken"] = "7" }));
        });
        public string Text => Dispatcher.InvokeAsync(() => { var r = GetCurrentRenderTreeFrames(root); return string.Join(" ", r.Array.Take(r.Count).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.FrameType == RenderTreeFrameType.Markup ? f.MarkupContent : "")); }).Result;
    }
    [Fact]
    public async Task Interactive_polling_updates_then_stops_at_terminal_and_dispose()
    {
        var s = new Server { Exists = true };
        using var sp = new ServiceCollection().AddLogging().AddSingleton(Client(s)).BuildServiceProvider();
        await using var renderer = new LiveRenderer(sp);
        await renderer.OpenAsync();
        s.Status = "Completed";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!renderer.Text.Contains("Đánh giá hoàn thành")) await Task.Delay(30, timeout.Token);
        var reads = s.Reads;
        await Task.Delay(2200);
        Assert.Equal(reads, s.Reads);
        Assert.Equal(0, s.Posts);
        await renderer.DisposeAsync();
        await Task.Delay(2100);
        Assert.Equal(reads, s.Reads);
    }
    private sealed class Server : HttpMessageHandler
    {
        public int Posts, Reads;
        public string Status = "Queued";
        public bool Exists, FailRead, LosePost, Reject;
        public TaskCompletionSource? Gate;
        public List<EvaluationSetDto> Sets = [new() { EvaluationSetId = 2, Name = "Bộ HR", Description = "Có nhãn", LabelCount = 10, PassCount = 6 }];
        public StartPromptTestRequest? Request;
        public PromptTestDetailDto Run => new() { RunId = 7, PromptVersionId = 1, EvaluationSetId = 2, PromptVersionName = "v1", Status = Status, TotalItems = 10, ProcessedItems = Status == "Queued" ? 0 : Status == "Running" ? 6 : 10, FailedItems = 1, AccuracyPercent = Status == "Completed" ? 75 : null };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Posts++; Request = await request.Content!.ReadFromJsonAsync<StartPromptTestRequest>(ct);
                if (Gate is not null) await Gate.Task.WaitAsync(ct);
                if (Reject) return Json(new { detail = "Bộ CV không hợp lệ" }, HttpStatusCode.BadRequest);
                Exists = true;
                if (LosePost) throw new HttpRequestException("lost response");
                return Json(Run, HttpStatusCode.Created);
            }
            if (path == "/api/evaluation-sets") return Json(Sets);
            if (path == "/api/prompt-tests") return Json(Exists ? new[] { Run } : []);
            Reads++;
            return FailRead ? Json(new { detail = "Mất kết nối" }, HttpStatusCode.ServiceUnavailable) : Json(Run);
        }
        private static HttpResponseMessage Json<T>(T data, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = JsonContent.Create(data) };
    }
    private static PromptApiClient Client(Server server) => new(new HttpClient(server) { BaseAddress = new Uri("http://api.test/") }, NullLogger<PromptApiClient>.Instance);
    [Theory]
    [InlineData(0, 0, false)] [InlineData(10, 11, false)] [InlineData(10, 10, false)] [InlineData(10, 0, false)] [InlineData(10, 6, true)]
    public void Set_requires_two_valid_label_classes(int total, int pass, bool expected) => Assert.Equal(expected, PromptTestFlow.ValidSet(new() { LabelCount = total, PassCount = pass }));
    [Theory]
    [InlineData(0, 1, 0)] [InlineData(10, -1, 0)] [InlineData(10, 6, 60)] [InlineData(10, 15, 100)]
    public void Percent_is_safe(int total, int processed, decimal expected) => Assert.Equal(expected, PromptTestFlow.Percent(new() { TotalItems = total, ProcessedItems = processed }));
    [Fact]
    public async Task Start_posts_exact_ids_once_and_progress_comes_only_from_api()
    {
        var s = new Server { Gate = new() }; var f = new PromptTestFlow(Client(s));
        await f.RestoreAsync(null, default);
        string? intent = null; f.Changed = () => { intent = f.Token; return Task.CompletedTask; };
        var pending = f.StartAsync(1, 2, default);
        Assert.Equal("pending.1.2.0", intent);
        await f.StartAsync(1, 2, default); Assert.Equal(1, s.Posts);
        s.Gate.SetResult(); await pending;
        Assert.Equal((1, 2), (s.Request!.PromptVersionId, s.Request.EvaluationSetId));
        Assert.Equal("7", f.Token); Assert.True(f.Running);
        s.Status = "Running"; await f.RefreshAsync(default); Assert.Equal(60, PromptTestFlow.Percent(f.Current!));
        s.Status = "Completed"; await f.RefreshAsync(default); Assert.False(f.Running); Assert.Equal(75, f.Current!.AccuracyPercent); Assert.Equal(1, f.Current.FailedItems);
    }
    [Theory]
    [InlineData("7")] [InlineData("")] [InlineData("pending.1.2.0")]
    public async Task Reload_restores_existing_run_without_post(string token)
    { var s = new Server { Exists = true }; var f = new PromptTestFlow(Client(s)); await f.RestoreAsync(token, default); Assert.True(f.Running); Assert.Equal(0, s.Posts); }
    [Fact]
    public async Task Lost_post_response_is_reconciled_without_reposting()
    { var s = new Server { LosePost = true }; var f = new PromptTestFlow(Client(s)); await f.RestoreAsync(null, default); await f.StartAsync(1, 2, default); Assert.True(f.Running); Assert.False(f.Uncertain); await f.StartAsync(1, 2, default); Assert.Equal(1, s.Posts); }
    [Fact]
    public async Task Pending_unknown_request_blocks_new_post_across_reload()
    { var s = new Server(); var f = new PromptTestFlow(Client(s)); await f.RestoreAsync("pending.1.2.0", default); Assert.True(f.Locked); await f.StartAsync(1, 2, default); Assert.Equal(0, s.Posts); s.Exists = true; await f.RefreshAsync(default); Assert.True(f.Running); }
    [Fact]
    public async Task Http_failure_preserves_progress_and_retry_is_get_only()
    { var s = new Server { Exists = true }; var f = new PromptTestFlow(Client(s)); await f.RestoreAsync("7", default); s.FailRead = true; await f.RefreshAsync(default); Assert.NotNull(f.Current); Assert.NotNull(f.Error); s.FailRead = false; s.Status = "Failed"; await f.RefreshAsync(default); Assert.Equal("Failed", f.Current!.Status); Assert.False(f.Running); Assert.Equal(0, s.Posts); }
    [Fact]
    public async Task Rejected_creation_is_not_reported_as_success()
    { var s = new Server { Reject = true }; var f = new PromptTestFlow(Client(s)); await f.RestoreAsync(null, default); await f.StartAsync(1, 2, default); Assert.Null(f.Current); Assert.NotNull(f.Error); Assert.False(f.Uncertain); }
    [Fact]
    public async Task Cancellation_does_not_send_second_post()
    { var s = new Server { Gate = new() }; var f = new PromptTestFlow(Client(s)); await f.RestoreAsync(null, default); using var cts = new CancellationTokenSource(); var task = f.StartAsync(1, 2, cts.Token); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); Assert.False(f.Starting); Assert.True(f.Uncertain); Assert.Equal(1, s.Posts); }
    [Fact]
    public async Task Component_renders_real_set_counts_and_completed_result()
    {
        var s = new Server { Exists = true, Status = "Completed" };
        using var sp = new ServiceCollection().AddLogging().AddSingleton(Client(s)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(sp, sp.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<PromptTestPanel>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["RunToken"] = "7", ["Versions"] = new List<PromptVersionDto> { new() { PromptVersionId = 1, VersionName = "v1" } } }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Contains("75%", html); Assert.Contains("6 Đạt", html); Assert.Contains("4 Không đạt", html); Assert.Contains("Đánh giá hoàn thành", html); Assert.Contains("1 CV lỗi", html); Assert.Contains("progressbar", html); Assert.Equal(0, s.Posts);
    }
}
#pragma warning restore BL0006
