extern alias web;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;
using web::SmartCv.Web.Components.Prompts;

namespace SmartCv.Tests;

#pragma warning disable BL0006
public class MismatchAnalysisTests
{
    private static PromptTestEvaluationDto Item(int id, bool? hr = true, bool? ai = true, string status = "Completed",
        string extraction = "Completed", bool inconsistent = false, int? evaluationId = null, string? source = null) => new()
    {
        ApplicationId = id, EvaluationId = evaluationId ?? id, HrIsPass = hr, PredictedPass = ai,
        Status = status, ExtractionStatus = extraction, ScoreSource = source,
        IsMatch = inconsistent ? true : hr is null || ai is null || status != "Completed" || extraction == "Failed" ? null : hr == ai,
        MismatchType = inconsistent ? null : hr is null || ai is null || status != "Completed" || extraction == "Failed" || hr == ai ? null : hr == true ? "Missed" : "WrongPick",
        CandidateName = id == 6 ? "Trần Thị Bình" : id == 7 ? "Hoàng Thị Em" : $"Ứng viên {id}",
        FileName = $"CV_{id}.pdf", JobTitle = ".NET Developer", FinalScore = id == 6 ? 66 : 72.5m,
        Reasoning = "Nhận xét gốc AI\nGiữ nguyên xuống dòng.", HrNote = "Ghi chú gốc HR", FitLabel = id == 6 ? "Tiềm năng" : "Phù hợp"
    };
    private static List<PromptTestEvaluationDto> Seed() =>
        Enumerable.Range(1, 5).Select(i => Item(i)).Concat([Item(6, true, false), Item(7, false, true)])
            .Concat(Enumerable.Range(8, 3).Select(i => Item(i, false, false))).ToList();
    private static PromptTestProgressDto Run(int id = 2, List<PromptTestEvaluationDto>? items = null, string status = "Completed", int? total = null, int version = 2) => new()
    {
        RunId = id, PromptVersionId = version, PromptVersionName = $"v{version}", EvaluationSetId = 1,
        EvaluationSetName = "Bộ CV đánh giá", Status = status, Items = items ?? Seed(),
        TotalItems = total ?? (items?.Count ?? 10), CompletedAt = new DateTime(2026, 11, id), CreatedAt = new DateTime(2026, 11, 1)
    };
    private static readonly List<PromptVersionDto> Versions = [new() { PromptVersionId = 2, VersionName = "v2", IsActive = true }];

    [Fact]
    public void Seed_matrix_filters_and_original_notes_are_correct()
    {
        var data = Run(); var view = MismatchView.Create(data);
        Assert.Equal((5, 1, 1, 3), (view.TruePositive, view.FalsePositive, view.FalseNegative, view.TrueNegative));
        Assert.True(view.Complete); Assert.False(view.AllMatch); Assert.Equal(10, view.Compared);
        Assert.Equal(2, view.Count(MismatchFilter.All));
        Assert.Equal("Trần Thị Bình", Assert.Single(view.Filter(MismatchFilter.Missed)).CandidateName);
        Assert.Equal("Hoàng Thị Em", Assert.Single(view.Filter(MismatchFilter.WrongPick)).CandidateName);
        Assert.Equal(66m, view.Mismatches[0].FinalScore); Assert.Equal(72.5m, view.Mismatches[1].FinalScore);
        Assert.Equal(view.FalsePositive + view.FalseNegative, view.Mismatches.Count);
        Assert.Equal("Nhận xét gốc AI\nGiữ nguyên xuống dòng.", data.Items[5].Reasoning);
    }
    [Theory]
    [InlineData(null, false, "Completed", "Completed")]
    [InlineData(true, null, "Completed", "Completed")]
    [InlineData(true, false, "Pending", "Completed")]
    [InlineData(true, false, "Failed", "Completed")]
    [InlineData(true, false, "Completed", "Failed")]
    public void Missing_or_failed_prediction_is_never_negative(bool? hr, bool? ai, string status, string extraction)
    {
        var view = MismatchView.Create(Run(items: [Item(1, hr, ai, status, extraction)]));
        Assert.Equal(0, view.Compared); Assert.Equal(1, view.Excluded); Assert.Empty(view.Mismatches);
        Assert.False(view.AllMatch);
    }
    [Fact]
    public void Inconsistent_flags_are_reported_without_rewriting_server_data()
    {
        var item = Item(1, true, false, inconsistent: true);
        var view = MismatchView.Create(Run(items: [item]));
        Assert.Equal(1, view.FalseNegative); Assert.Equal(1, view.InconsistentFlags);
        Assert.Empty(view.Mismatches); Assert.False(view.Complete); Assert.True(item.IsMatch);
    }
    [Fact]
    public void Duplicate_applications_are_excluded_and_keyword_prediction_is_valid()
    {
        var view = MismatchView.Create(Run(items: [Item(1), Item(1), Item(2, false, true, source: "KeywordOnly")]));
        Assert.Equal(1, view.Compared); Assert.Equal(2, view.Excluded); Assert.Equal(1, view.DuplicateApplications);
        Assert.Equal(1, view.KeywordOnly); Assert.Equal(1, view.FalsePositive); Assert.Single(view.Mismatches);
    }
    [Theory]
    [InlineData("Running", 1, false)] [InlineData("Failed", 1, false)]
    [InlineData("Completed", 2, false)] [InlineData("Completed", 1, true)]
    public void All_match_requires_completed_full_consistent_data(string status, int total, bool expected) =>
        Assert.Equal(expected, MismatchView.Create(Run(items: [Item(1)], status: status, total: total)).AllMatch);
    [Fact]
    public void Empty_or_invalid_application_never_claims_perfect_match()
    {
        Assert.False(MismatchView.Create(Run(items: [])).AllMatch);
        Assert.Equal(0, MismatchView.Create(Run(items: [Item(0)])).Compared);
    }
    [Fact]
    public void Default_prefers_active_completed_then_latest_with_stable_ties()
    {
        var runs = new[] { Run(1, version: 2), Run(2, version: 1), Run(3, status: "Running"), Run(4, status: "Failed") };
        Assert.Equal(1, MismatchView.DefaultRun(runs, 2)!.RunId);
        Assert.Equal(2, MismatchView.DefaultRun(runs, null)!.RunId);
        Assert.Null(MismatchView.DefaultRun([Run(status: "Running")], 2));
        Assert.Equal(8, MismatchView.DefaultRun([new() { RunId = 7, Status = "Completed" }, new() { RunId = 8, Status = "Completed" }], null)!.RunId);
    }
    [Fact]
    public void Detail_must_exist_and_belong_to_the_exact_run_and_application()
    {
        var item = Item(6);
        Assert.True(MismatchView.SupportsDetail(2, item, new() { EvaluationId = 6, RunId = 2, RunType = "PromptTest", ApplicationId = 6 }));
        Assert.False(MismatchView.SupportsDetail(2, item, new() { EvaluationId = 6, RunId = 1, RunType = "PromptTest", ApplicationId = 6 }));
        Assert.False(MismatchView.SupportsDetail(2, Item(6, evaluationId: 0), new()));
    }

    private sealed class Server : HttpMessageHandler
    {
        public Dictionary<int, PromptTestProgressDto> Runs = new() { [1] = Run(1, [Item(11, true, false), Item(12, false, true), Item(13, false, true)], version: 1), [2] = Run() };
        public List<string> Paths = [];
        public bool Fail, WrongRun, InvalidDetails;
        public TaskCompletionSource? WaitForOld, ReleaseOld;
        public CancellationToken LastToken;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            var path = req.RequestUri!.AbsolutePath; Paths.Add(path); LastToken = ct;
            if (Fail) return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { detail = "API tạm mất kết nối" }) };
            object data;
            if (path == "/api/prompt-tests") data = Runs.Values.ToList();
            else if (path.StartsWith("/api/evaluations/"))
            {
                var id = int.Parse(path.Split('/').Last());
                var run = Runs.Values.FirstOrDefault(r => r.Items.Any(i => i.EvaluationId == id));
                if (run is null) return new(HttpStatusCode.NotFound);
                data = new EvaluationDetailDto { EvaluationId = id, ApplicationId = id, RunId = InvalidDetails ? 999 : run.RunId, RunType = "PromptTest" };
            }
            else
            {
                var id = int.Parse(path.Split('/').Last());
                if (id == 1 && WaitForOld is not null) { WaitForOld.TrySetResult(); await ReleaseOld!.Task; }
                data = WrongRun ? Run(9) : Runs[id];
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(data) };
        }
    }
    private static ServiceProvider Services(Server server) => new ServiceCollection().AddLogging()
        .AddSingleton(new PromptApiClient(new HttpClient(server) { BaseAddress = new Uri("https://fixture.test/") }, NullLogger<PromptApiClient>.Instance)).BuildServiceProvider();
    private static async Task<string> Render(Server server, int? id = null)
    {
        using var services = Services(server);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<MismatchTable>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Versions"] = Versions, ["RunId"] = id }))).ToHtmlString()));
    }
    [Fact]
    public async Task Component_defaults_to_active_run_shows_notes_scores_and_verified_links()
    {
        var server = new Server(); var html = await Render(server);
        Assert.Contains("#2", html); Assert.Contains("TP · Đúng", html); Assert.Contains("FN · Bỏ sót", html);
        Assert.Contains("Trần Thị Bình", html); Assert.Contains("Hoàng Thị Em", html); Assert.Contains("72,5 điểm", html);
        Assert.Contains("Ghi chú gốc HR", html); Assert.Contains("Nhận xét gốc AI", html);
        Assert.Contains("href=\"/results/evaluations/6\"", html);
        Assert.DoesNotContain("/api/prompt-tests/1", server.Paths);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Invalid_or_absent_evaluation_id_does_not_create_dead_link(bool invalid)
    {
        var server = new Server { InvalidDetails = invalid };
        if (!invalid) server.Runs[2] = Run(items: [Item(6, true, false, evaluationId: 0)]);
        var html = await Render(server, 2);
        Assert.DoesNotContain("href=\"/results/evaluations/", html);
        Assert.Contains("chưa có chi tiết được xác minh", html);
        if (!invalid) Assert.DoesNotContain(server.Paths, p => p.StartsWith("/api/evaluations/"));
    }
    [Theory]
    [InlineData("Running")] [InlineData("Queued")] [InlineData("Failed")]
    public async Task Unfinished_run_never_shows_complete_matrix_or_all_match(string status)
    {
        var server = new Server(); server.Runs[2] = Run(status: status);
        var html = await Render(server, 2);
        Assert.Contains("Chưa có kết quả hoàn chỉnh", html);
        Assert.DoesNotContain("class=\"matrix\"", html); Assert.DoesNotContain("AI khớp HR ở tất cả", html);
    }
    [Fact]
    public async Task Api_error_and_wrong_run_are_explicit_without_stale_rows()
    {
        Assert.Contains("API tạm mất kết nối", await Render(new() { Fail = true }, 2));
        var html = await Render(new() { WrongRun = true }, 2);
        Assert.Contains("sai lượt đánh giá", html); Assert.DoesNotContain("Trần Thị Bình", html);
    }
    [Fact]
    public async Task No_runs_and_complete_match_have_distinct_empty_states()
    {
        Assert.Contains("Chưa có lượt đánh giá hoàn thành", await Render(new() { Runs = [] }));
        var server = new Server(); server.Runs[2] = Run(items: [Item(1)]);
        Assert.Contains("AI khớp HR ở tất cả", await Render(server, 2));
        server.Runs[2] = Run(items: [Item(1, true, null)]);
        var html = await Render(server, 2);
        Assert.Contains("Chưa đủ dữ liệu nhất quán", html); Assert.DoesNotContain("AI khớp HR ở tất cả", html);
    }
    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        private int root = -1;
        public Task Show(int? id, int refresh = 0) => Dispatcher.InvokeAsync(async () =>
        {
            if (root < 0) root = AssignRootComponentId(InstantiateComponent(typeof(MismatchTable)));
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?> { ["RunId"] = id, ["Versions"] = Versions, ["RefreshVersion"] = refresh }));
        });
        public string Text()
        {
            var sb = new StringBuilder(); var frames = GetCurrentRenderTreeFrames(root);
            for (var i = 0; i < frames.Count; i++)
            { var f = frames.Array[i]; if (f.FrameType == RenderTreeFrameType.Text) sb.Append(f.TextContent); if (f.FrameType == RenderTreeFrameType.Markup) sb.Append(f.MarkupContent); }
            return WebUtility.HtmlDecode(sb.ToString());
        }
        public Task Filter(string label) => Dispatcher.InvokeAsync(() =>
        {
            var frames = GetCurrentRenderTreeFrames(root);
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element || frames.Array[i].ElementName != "button") continue;
                var end = i + frames.Array[i].ElementSubtreeLength;
                var text = string.Concat(frames.Array.Skip(i).Take(end - i).Where(f => f.FrameType == RenderTreeFrameType.Text).Select(f => f.TextContent));
                if (!text.StartsWith(label)) continue;
                var ev = frames.Array.Skip(i).Take(end - i).First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick");
                return DispatchEventAsync(ev.AttributeEventHandlerId, null, new MouseEventArgs());
            }
            throw new InvalidOperationException("Missing filter button");
        });
    }
    [Fact]
    public async Task Filtering_is_local_matrix_stays_fixed_and_run_change_resets_filter()
    {
        var server = new Server(); using var services = Services(server); await using var renderer = new TestRenderer(services);
        await renderer.Show(2); var gets = server.Paths.Count;
        await renderer.Filter("Bỏ sót"); Assert.Contains("Trần Thị Bình", renderer.Text()); Assert.DoesNotContain("Hoàng Thị Em", renderer.Text());
        Assert.Contains("FP · Chọn nhầm", renderer.Text()); Assert.Equal(gets, server.Paths.Count);
        await renderer.Filter("Chọn nhầm"); Assert.Contains("Hoàng Thị Em", renderer.Text()); Assert.DoesNotContain("Trần Thị Bình", renderer.Text());
        await renderer.Filter("Tất cả CV lệch"); Assert.Contains("Trần Thị Bình", renderer.Text());
        await renderer.Filter("Bỏ sót"); await renderer.Show(1); Assert.Contains("Ứng viên 12", renderer.Text()); Assert.DoesNotContain("Trần Thị Bình", renderer.Text());
    }
    [Fact]
    public async Task Completion_signal_refreshes_without_new_polling_or_post()
    {
        var server = new Server(); using var services = Services(server); await using var renderer = new TestRenderer(services);
        await renderer.Show(2); var count = server.Paths.Count;
        await renderer.Show(2); Assert.Equal(count, server.Paths.Count);
        server.Runs[2] = Run(items: [Item(1)]); await renderer.Show(2, 1);
        Assert.Contains("AI khớp HR ở tất cả", renderer.Text()); Assert.True(server.Paths.Count > count);
    }
    [Fact]
    public async Task Old_response_cannot_replace_new_selected_run()
    {
        var server = new Server { WaitForOld = new(), ReleaseOld = new() };
        using var services = Services(server); await using var renderer = new TestRenderer(services);
        var old = renderer.Show(1); await server.WaitForOld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var fresh = renderer.Show(2);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (!server.Paths.Contains("/api/prompt-tests/2")) await Task.Delay(10, timeout.Token);
        server.ReleaseOld.SetResult(); await Task.WhenAll(old, fresh);
        Assert.Contains("Trần Thị Bình", renderer.Text()); Assert.DoesNotContain("Ứng viên 11", renderer.Text());
    }
    [Fact]
    public async Task Leaving_component_cancels_inflight_request()
    {
        var server = new Server { WaitForOld = new(), ReleaseOld = new() };
        using var services = Services(server); var renderer = new TestRenderer(services);
        var loading = renderer.Show(1); await server.WaitForOld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = renderer.DisposeAsync().AsTask(); Assert.True(server.LastToken.IsCancellationRequested);
        server.ReleaseOld.SetResult(); await dispose; await loading;
    }
}
#pragma warning restore BL0006
