extern alias web;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components.Results;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

#pragma warning disable BL0006
public class B34StageUiTests
{
    [B34DemoFact]
    public async Task Demo_twenty_PDFs_drive_UI_decisions_through_real_A35_HTTP_endpoints()
    {
        using var zip = ZipFile.OpenRead(System.Environment.GetEnvironmentVariable("SMARTCV_B34_DEMO_ZIP")!);
        var pdfs = zip.Entries.Where(e => e.FullName.Contains("cv-20-backend/") && e.Name.EndsWith(".pdf")).OrderBy(e => e.Name).ToList();
        Assert.Equal(20, pdfs.Count);
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" });
        using var http = factory.CreateClient();
        var cvIds = new List<Guid>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            await db.Database.EnsureCreatedAsync();
            foreach (var entry in pdfs)
            {
                await using var stream = entry.Open();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                var bytes = memory.ToArray();
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
                var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
                Assert.NotEmpty(text);
                var cv = new Cv
                {
                    CvId = Guid.NewGuid(), OriginalFileName = entry.Name, ContentType = "application/pdf", StoredPath = "test-only",
                    FileSizeBytes = bytes.Length, FileHash = Convert.ToHexString(SHA256.HashData(bytes)), ExtractionStatus = "Completed",
                    ExtractedText = text, ExtractionMethod = "PdfText", UploadedAt = DateTime.UtcNow,
                    Candidate = new Candidate { FullName = Path.GetFileNameWithoutExtension(entry.Name) }
                };
                db.Add(cv); cvIds.Add(cv.CvId);
            }
            await db.SaveChangesAsync();
        }
        using var assigned = await http.PostAsJsonAsync("/api/applications", new CreateApplicationsRequest { JobId = 1, CvIds = cvIds });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var assignments = (await assigned.Content.ReadFromJsonAsync<CreateApplicationsResponse>())!;
        var ids = assignments.Items.Select(i => i.ApplicationId!.Value).ToList();
        int runId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var run = new EvaluationRun
            {
                RunType = "Screening", Status = "Completed", TotalItems = 20, ProcessedItems = 20,
                ModelName = "fixture-without-Gemini", KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70,
                PromptVersion = new PromptVersion { VersionName = "B34 demo", SystemPrompt = "Test only" }, CreatedAt = DateTime.UtcNow,
                Evaluations = ids.Select(id => new Evaluation { ApplicationId = id, Status = "Completed", FinalScore = 80, FitLabel = "Good", PredictedPass = true }).ToList()
            };
            db.Add(run); await db.SaveChangesAsync(); runId = run.RunId;
        }
        var actor = new HrActorState();
        await actor.SetAsync("HR - Trần Thu Hà");
        using var services = new ServiceCollection().AddSingleton(actor).AddSingleton<IJSRuntime>(new NoJs())
            .AddSingleton(new ApplicationApiClient(http, NullLogger<ApplicationApiClient>.Instance)).BuildServiceProvider();
        await using var renderer = new LiveRenderer(services);
        async Task<bool> Reload()
        {
            var data = await http.GetFromJsonAsync<RankingResponse>($"/api/runs/{runId}/ranking?jobId=1");
            await renderer.Render(data!.Items, Reload);
            return true;
        }
        await Reload();
        Assert.Contains("Hiển thị 20/20 CV", renderer.Text);
        await renderer.Change($"stage-select-{ids[0]}", "Shortlisted");
        foreach (var i in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 18, 19 }) await renderer.Check(ids[i], true);
        await renderer.ClickId("bulk-shortlist");
        Assert.Contains("Đã chuyển 9 hồ sơ", renderer.Text);
        foreach (var i in new[] { 8, 9, 10, 13, 14, 15 }) await renderer.Check(ids[i], true);
        await renderer.ClickId("bulk-reject"); await renderer.Radio("MissingSkills"); await renderer.Click("Loại hồ sơ");
        await renderer.Check(ids[11], true); await renderer.Check(ids[12], true); await renderer.ClickId("bulk-hold");
        var ranking = (await http.GetFromJsonAsync<RankingResponse>($"/api/runs/{runId}/ranking?jobId=1"))!;
        Assert.Equal((10, 6, 2, 2), (ranking.StageCounts["Shortlisted"], ranking.StageCounts["Rejected"], ranking.StageCounts["OnHold"], ranking.StageCounts["New"]));
        using var verify = factory.Services.CreateScope();
        var savedDb = verify.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        Assert.Equal(18, await savedDb.ApplicationEvents.CountAsync());
        Assert.All(await savedDb.ApplicationEvents.ToListAsync(), e => Assert.Equal("HR - Trần Thu Hà", e.Actor));
        Assert.All(await savedDb.Evaluations.ToListAsync(), e => Assert.Equal(80m, e.FinalScore));
    }

    [Fact]
    public async Task First_decision_asks_actor_then_next_decision_saves_immediately_with_versions()
    {
        await using var env = await Environment.CreateAsync();
        await env.Renderer.Change("stage-select-1", "Shortlisted");
        Assert.Contains("Tên của bạn", env.Renderer.Text);
        Assert.Equal(0, env.Api.Writes);
        await env.Renderer.Input("Tên HR thực hiện quyết định", "  HR Hà  ");
        await env.Renderer.Click("Lưu quyết định");
        Assert.Equal(1, env.Api.Writes);
        Assert.Equal("HR Hà", env.Actor.Actor);
        Assert.Equal("New", env.Api.LastSingle!.ExpectedStage);
        Assert.NotNull(env.Api.LastSingle.ExpectedStageVersion);
        Assert.Equal("Shortlisted", env.Renderer.Value("stage-select-1"));
        await env.Renderer.Change("stage-select-2", "Interview");
        Assert.Equal(2, env.Api.Writes);
        Assert.Equal("Interview", env.Renderer.Value("stage-select-2"));
        Assert.DoesNotContain("stage-dialog-title", env.Renderer.Attribute("id"));
        Assert.Equal(2, env.Refreshes);
    }

    [Fact]
    public async Task Reject_requires_reason_cancel_restores_value_and_note_and_actor_are_sent()
    {
        await using var env = await Environment.CreateAsync(actor: "HR");
        await env.Renderer.Change("stage-select-1", "Rejected");
        Assert.Equal(6, env.Renderer.Attribute("name").Count(n => n == "stage-reason"));
        await env.Renderer.Click("Loại hồ sơ");
        Assert.Contains("Hãy chọn lý do loại.", env.Renderer.Text);
        Assert.Equal(0, env.Api.Writes);
        await env.Renderer.Click("Hủy");
        Assert.Equal("New", env.Renderer.Value("stage-select-1"));
        await env.Renderer.Change("stage-select-1", "Rejected");
        await env.Renderer.Radio("MissingSkills");
        await env.Renderer.Input("Ghi chú quyết định", "Thiếu SQL Server");
        await env.Renderer.Click("Loại hồ sơ");
        Assert.Equal("MissingSkills", env.Api.LastSingle!.RejectReason);
        Assert.Equal("Thiếu SQL Server", env.Api.LastSingle.Note);
        Assert.Contains("Thiếu kỹ năng bắt buộc", env.Renderer.Text);
        await env.Renderer.ClickId("stage-reason-1");
        await env.Renderer.Radio("Duplicate");
        await env.Renderer.Click("Loại hồ sơ");
        Assert.Equal("Duplicate", env.Api.LastSingle.RejectReason);
        Assert.Equal("Rejected", env.Api.LastSingle.ExpectedStage);
    }

    [Fact]
    public async Task Twenty_candidate_workflow_filters_bulk_counts_noops_and_reopens_rejected()
    {
        await using var env = await Environment.CreateAsync(20, "HR - Trần Thu Hà");
        await env.Renderer.Change("stage-select-1", "Shortlisted");
        // Ten applications including the first, already shortlisted.
        foreach (var id in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 19, 20 }) await env.Renderer.Check(id, true);
        await env.Renderer.ClickId("bulk-shortlist");
        Assert.Contains("Đã chuyển 9 hồ sơ", env.Renderer.Text);
        Assert.Contains("1 hồ sơ đã ở giai đoạn này", env.Renderer.Text);
        Assert.Equal(10, env.Api.Items.Count(r => r.Stage == "Shortlisted"));
        Assert.Equal(10, env.Api.LastBulk!.ExpectedStageVersions!.Count);
        foreach (var id in new[] { 9, 10, 11, 14, 15, 16 }) await env.Renderer.Check(id, true);
        await env.Renderer.ClickId("bulk-reject");
        await env.Renderer.Radio("MissingSkills");
        await env.Renderer.Click("Loại hồ sơ");
        await env.Renderer.Check(12, true); await env.Renderer.Check(13, true);
        await env.Renderer.ClickId("bulk-hold");
        Assert.Equal(2, env.Api.Items.Count(r => r.Stage == "OnHold"));
        await env.Renderer.Click("Qua vòng CV", "stage-filter");
        Assert.Equal(10, env.Renderer.Attribute("id").Count(v => v.StartsWith("stage-select-")));
        Assert.Contains("Hiển thị 10/20 CV", env.Renderer.Text);
        await env.Renderer.Click("Tất cả", "stage-filter");
        await env.Renderer.Change("stage-select-9", "Reviewing");
        Assert.Null(env.Api.LastSingle!.RejectReason);
        Assert.Equal("Reviewing", env.Api.Items.Single(r => r.ApplicationId == 9).Stage);
        Assert.DoesNotContain("Ẩn danh", env.Renderer.Text);
    }

    [Fact]
    public async Task Select_all_only_includes_visible_search_results_and_filters_clear_selection()
    {
        await using var env = await Environment.CreateAsync(20, "HR");
        await env.Renderer.Input("Tìm ứng viên theo tên hoặc tên file", "Ứng viên 12");
        await env.Renderer.CheckAll(true);
        await env.Renderer.ClickId("bulk-hold");
        Assert.Equal(new[] { 12 }, env.Api.LastBulk!.ApplicationIds);
        await env.Renderer.Input("Tìm ứng viên theo tên hoặc tên file", "");
        await env.Renderer.Check(1, true);
        await env.Renderer.Click("Dự phòng", "stage-filter");
        Assert.DoesNotContain("bulk-hold", env.Renderer.Attribute("id"));
        await env.Renderer.Input("Tìm ứng viên theo tên hoặc tên file", "not found");
        Assert.Contains("Không có CV nào khớp bộ lọc", env.Renderer.Text);
        await env.Renderer.Click("Xóa bộ lọc");
        Assert.Contains("Hiển thị 20/20 CV", env.Renderer.Text);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task Api_failure_preserves_saved_state_and_refreshes_without_retrying_write(int status)
    {
        await using var env = await Environment.CreateAsync(actor: "HR");
        env.Api.Failure = status;
        await env.Renderer.Change("stage-select-1", "Interview");
        Assert.Equal("New", env.Renderer.Value("stage-select-1"));
        Assert.Contains("alert", env.Renderer.Attribute("role"));
        if (status == 409) Assert.Contains("Hồ sơ vừa được đổi", env.Renderer.Text);
        Assert.Equal(1, env.Api.Writes);
        Assert.Equal(1, env.Refreshes);
        Assert.DoesNotContain("Đã chuyển hồ sơ", env.Renderer.Text);
    }

    [Fact]
    public async Task Failed_refresh_blocks_more_writes_until_explicit_get_retry_succeeds()
    {
        await using var env = await Environment.CreateAsync(actor: "HR");
        env.RefreshFails = true;
        await env.Renderer.Change("stage-select-1", "Shortlisted");
        Assert.Contains("Chưa đồng bộ được", env.Renderer.Text);
        Assert.True(env.Renderer.Disabled("stage-select-1"));
        Assert.Equal("Shortlisted", env.Renderer.Value("stage-select-1"));
        env.RefreshFails = false;
        await env.Renderer.Click("Tải lại giai đoạn");
        Assert.False(env.Renderer.Disabled("stage-select-1"));
        Assert.Equal(1, env.Api.Writes);
    }

    [Fact]
    public async Task Bulk_conflict_has_no_optimistic_success_or_partial_overrides()
    {
        await using var env = await Environment.CreateAsync(actor: "HR");
        await env.Renderer.CheckAll(true);
        env.Api.Failure = 409;
        await env.Renderer.ClickId("bulk-shortlist");
        Assert.Contains("Hồ sơ vừa được đổi", env.Renderer.Text);
        Assert.Equal("New", env.Renderer.Value("stage-select-1"));
        Assert.Equal("New", env.Renderer.Value("stage-select-2"));
        Assert.DoesNotContain("Đã chuyển", env.Renderer.Text);
    }

    [Fact]
    public async Task Repeated_change_events_while_request_pending_send_exactly_one_write()
    {
        await using var env = await Environment.CreateAsync(actor: "HR");
        env.Api.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = env.Renderer.Change("stage-select-1", "Shortlisted");
        await env.Renderer.Dispatcher.InvokeAsync(() => { });
        Assert.Equal(1, env.Api.Writes);
        Assert.True(env.Renderer.Disabled("stage-select-1"));
        await env.Renderer.Change("stage-select-2", "Interview");
        Assert.Equal(1, env.Api.Writes);
        env.Api.Gate.SetResult();
        await first;
        Assert.False(env.Renderer.Disabled("stage-select-1"));
    }

    [Fact]
    public async Task Offline_failure_is_uncertain_and_never_retries_the_write()
    {
        await using var env = await Environment.CreateAsync(actor: "HR");
        env.Api.Offline = true;
        await env.Renderer.Change("stage-select-1", "Interview");
        Assert.Contains("Chưa xác nhận được kết quả lưu", env.Renderer.Text);
        Assert.Equal(1, env.Api.Writes);
        Assert.Equal(1, env.Refreshes);
    }

    [Fact]
    public async Task Bulk_reports_missing_application_separately_and_keeps_other_successes()
    {
        await using var env = await Environment.CreateAsync(actor: "HR");
        env.Api.MissingId = 2;
        await env.Renderer.CheckAll(true);
        await env.Renderer.ClickId("bulk-shortlist");
        Assert.Contains("Đã chuyển 1 hồ sơ", env.Renderer.Text);
        Assert.Contains("Không tìm thấy: #2", env.Renderer.Text);
        Assert.Equal("Shortlisted", env.Renderer.Value("stage-select-1"));
    }

    [Fact]
    public async Task Selection_is_limited_to_200_without_silently_splitting_bulk_requests()
    {
        await using var env = await Environment.CreateAsync(201, "HR");
        for (var i = 1; i <= 201; i++) await env.Renderer.Check(i, true);
        Assert.Contains("Mỗi lần chỉ được chọn tối đa 200", env.Renderer.Text);
        await env.Renderer.ClickId("bulk-shortlist");
        Assert.Equal(200, env.Api.LastBulk!.ApplicationIds.Count);
        Assert.Equal(1, env.Api.Writes);
    }

    [Fact]
    public async Task Actor_can_be_changed_without_creating_a_decision()
    {
        await using var env = await Environment.CreateAsync(actor: "HR 1");
        await env.Renderer.ClickId("change-hr");
        await env.Renderer.Input("Tên HR thực hiện quyết định", "HR 2");
        await env.Renderer.Click("Lưu tên HR");
        Assert.Equal("HR 2", env.Actor.Actor);
        Assert.Equal(0, env.Api.Writes);
    }

    private sealed class Environment : IAsyncDisposable
    {
        public readonly FakeStageApi Api;
        public readonly HrActorState Actor = new();
        private readonly ServiceProvider services;
        public readonly LiveRenderer Renderer;
        public int Refreshes;
        public bool RefreshFails;
        private Environment(int count)
        {
            Api = new(count);
            services = new ServiceCollection().AddSingleton(Actor)
                .AddSingleton(new ApplicationApiClient(new HttpClient(Api) { BaseAddress = new("https://api.test/") }, NullLogger<ApplicationApiClient>.Instance))
                .AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
            Renderer = new(services);
        }
        public static async Task<Environment> CreateAsync(int count = 2, string? actor = null)
        {
            var env = new Environment(count);
            if (actor is not null) await env.Actor.SetAsync(actor);
            await env.Renderer.Render(env.Api.Snapshot(), env.Refresh);
            return env;
        }
        private async Task<bool> Refresh()
        {
            Refreshes++;
            if (RefreshFails) return false;
            await Renderer.Render(Api.Snapshot(), Refresh);
            return true;
        }
        public async ValueTask DisposeAsync() { await Renderer.DisposeAsync(); await services.DisposeAsync(); }
    }

    private sealed class FakeStageApi(int count) : HttpMessageHandler
    {
        public List<RankingItemDto> Items = Enumerable.Range(1, count).Select(id => Row(id)).ToList();
        public int Writes;
        public int? Failure;
        public bool Offline;
        public int? MissingId;
        public TaskCompletionSource? Gate;
        public ChangeStageRequest? LastSingle;
        public BulkChangeStageRequest? LastBulk;
        public List<RankingItemDto> Snapshot() => JsonSerializer.Deserialize<List<RankingItemDto>>(JsonSerializer.Serialize(Items))!;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Writes++;
            if (Offline) throw new HttpRequestException("Synthetic offline test");
            if (request.Method == HttpMethod.Patch) LastSingle = await request.Content!.ReadFromJsonAsync<ChangeStageRequest>(ct);
            else LastBulk = await request.Content!.ReadFromJsonAsync<BulkChangeStageRequest>(ct);
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            if (Failure is { } status) return new((HttpStatusCode)status) { Content = JsonContent.Create(new { detail = "Quyết định không được lưu.", rolledBack = true }) };
            if (LastSingle is { } one && request.Method == HttpMethod.Patch)
            {
                var id = int.Parse(request.RequestUri!.Segments[^2].Trim('/'));
                return Json(Apply(id, one.ToStage, one.RejectReason));
            }
            var bulk = LastBulk!;
            var results = bulk.ApplicationIds.Select(id => Apply(id, bulk.ToStage, bulk.RejectReason)).ToList();
            return Json(new BulkStageChangeResponse { Total = results.Count, Changed = results.Count(r => r.Outcome == "Changed"), Unchanged = results.Count(r => r.Outcome == "Unchanged"), NotFound = results.Count(r => r.Outcome == "NotFound"), Results = results });
        }
        private StageChangeResultDto Apply(int id, string stage, string? reason)
        {
            if (id == MissingId) return new() { ApplicationId = id, Outcome = "NotFound" };
            var row = Items.Single(r => r.ApplicationId == id);
            var changed = row.Stage != stage || row.RejectReason != reason;
            var version = changed ? Guid.NewGuid() : row.StageVersion;
            Items[Items.IndexOf(row)] = Row(id, stage, reason, version);
            return new() { ApplicationId = id, Outcome = changed ? "Changed" : "Unchanged", Stage = stage, FromStage = row.Stage, RejectReason = reason, StageVersion = version, EventId = changed ? id : null };
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        private static RankingItemDto Row(int id, string stage = "New", string? reason = null, Guid? version = null) => new()
        {
            ApplicationId = id, EvaluationId = id, Rank = id, FileName = $"cv-{id:00}.pdf", CandidateName = $"Ứng viên {id}",
            Stage = stage, RejectReason = reason, StageVersion = version ?? Guid.NewGuid(), Status = "Completed", FinalScore = 80, FitLabel = "Good", PredictedPass = true
        };
    }

    private sealed class NoJs : IJSRuntime, IJSObjectReference
    {
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => ValueTask.FromResult(id == "import" ? (T)(object)this : default!);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class LiveRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", true);
        protected override void HandleException(Exception e) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        private int root = -1;
        public Task Render(List<RankingItemDto> rows, Func<Task<bool>> refresh) => Dispatcher.InvokeAsync(async () =>
        {
            if (root < 0) root = AssignRootComponentId(InstantiateComponent(typeof(RankingTable)));
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(RankingTable.Items)] = rows, [nameof(RankingTable.ReloadStages)] = refresh, [nameof(RankingTable.RunId)] = 1, [nameof(RankingTable.JobId)] = 1 }));
        });
        private IEnumerable<RenderTreeFrame[]> Components(int id)
        {
            var range = GetCurrentRenderTreeFrames(id);
            var frames = range.Array.Take(range.Count).ToArray();
            yield return frames;
            foreach (var component in frames.Where(f => f.FrameType == RenderTreeFrameType.Component))
                foreach (var child in Components(component.ComponentId)) yield return child;
        }
        private static string? Attr(RenderTreeFrame[] frames, int i, string name) => frames.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).FirstOrDefault(f => f.AttributeName == name).AttributeValue?.ToString();
        private static string TextOf(IEnumerable<RenderTreeFrame> frames) => System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(string.Concat(frames.Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.FrameType == RenderTreeFrameType.Markup ? System.Text.RegularExpressions.Regex.Replace(f.MarkupContent, "<[^>]+>", " ") : " "))), @"\s+", " ").Trim();
        public string Text => Dispatcher.InvokeAsync(() => TextOf(Components(root).SelectMany(f => f))).Result;
        public List<string> Attribute(string name) => Dispatcher.InvokeAsync(() => Components(root).SelectMany(f => f).Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == name).Select(f => f.AttributeValue?.ToString() ?? "").ToList()).Result;
        public string? Value(string id) => Dispatcher.InvokeAsync(() => Find((f, i) => Attr(f, i, "id") == id, (f, i) => Attr(f, i, "value"))).Result;
        public bool Disabled(string id) => Dispatcher.InvokeAsync(() => Find((f, i) => Attr(f, i, "id") == id, (f, i) => Attr(f, i, "disabled") == "True")).Result;
        private T Find<T>(Func<RenderTreeFrame[], int, bool> match, Func<RenderTreeFrame[], int, T> get)
        {
            foreach (var frames in Components(root)) for (var i = 0; i < frames.Length; i++) if (frames[i].FrameType == RenderTreeFrameType.Element && match(frames, i)) return get(frames, i);
            throw new Xunit.Sdk.XunitException("Element not found.");
        }
        private Task Event(Func<RenderTreeFrame[], int, bool> match, string name, EventArgs args) => Dispatcher.InvokeAsync(() =>
            DispatchEventAsync(Find(match, (f, i) => f.Skip(i + 1).TakeWhile(a => a.FrameType == RenderTreeFrameType.Attribute).Single(a => a.AttributeName == name).AttributeEventHandlerId), null, args));
        public Task Change(string id, string value) => Event((f, i) => Attr(f, i, "id") == id, "onchange", new ChangeEventArgs { Value = value });
        public Task Input(string label, string value) => Event((f, i) => Attr(f, i, "aria-label") == label, "oninput", new ChangeEventArgs { Value = value });
        public Task Check(int id, bool value) => Event((f, i) => (Attr(f, i, "aria-label") ?? "").StartsWith($"Chọn hồ sơ {id}:"), "onchange", new ChangeEventArgs { Value = value });
        public Task CheckAll(bool value) => Event((f, i) => Attr(f, i, "aria-label") == "Chọn mọi hồ sơ đang hiển thị", "onchange", new ChangeEventArgs { Value = value });
        public Task Radio(string value) => Event((f, i) => Attr(f, i, "type") == "radio" && Attr(f, i, "value") == value, "onchange", new ChangeEventArgs { Value = value });
        public Task ClickId(string id) => Event((f, i) => Attr(f, i, "id") == id, "onclick", new MouseEventArgs());
        public Task Click(string text, string? css = null) => Event((f, i) => f[i].ElementName == "button" && TextOf(f.Skip(i + 1).Take(f[i].ElementSubtreeLength - 1)).Contains(text) && (css is null || (Attr(f, i, "class") ?? "").Contains(css)), "onclick", new MouseEventArgs());
    }
}

public sealed class B34DemoFactAttribute : FactAttribute
{
    public B34DemoFactAttribute()
    {
        if (!File.Exists(System.Environment.GetEnvironmentVariable("SMARTCV_B34_DEMO_ZIP")))
            Skip = "Set SMARTCV_B34_DEMO_ZIP to run the 20 demo PDF integration scenario.";
    }
}
#pragma warning restore BL0006
