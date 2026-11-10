extern alias web;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Models;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>NavigationManager cho test: đổi địa chỉ và báo LocationChanged như trình duyệt (để ?runId hoạt động).</summary>
internal sealed class TestNavigationManager : NavigationManager
{
    public List<string> History { get; } = [];

    public TestNavigationManager(string uri = "https://app.test/upload") => Initialize("https://app.test/", uri);

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        var absolute = ToAbsoluteUri(uri).ToString();
        History.Add(absolute);
        Uri = absolute;
        NotifyLocationChanged(isInterceptedLink: false);
    }
}

/// <summary>
/// B6: nút "Phân tích hồ sơ" (POST /api/applications → POST /api/runs → /upload?runId=N)
/// và thẻ tiến độ (GET /api/runs/{id} mỗi 2 giây). API là HttpMessageHandler giả trong test; trang gọi đúng đường dẫn thật.
/// </summary>
#pragma warning disable BL0006 // RenderTree APIs chỉ dùng trong renderer kiểm thử.
public class RunProgressUiTests
{
    // ================= (1) Logic thuần: RunFlow =================

    private static JobDto Job(bool active = true) => new() { JobId = 1, Title = "Backend", IsActive = active };

    private static UploadItem Ok(string name) => new() { FileName = name, State = UploadItemState.Uploaded, CvId = Guid.NewGuid() };

    [Fact]
    public void Validate_requires_active_job_and_at_least_one_valid_selected_cv()
    {
        Assert.True(RunFlow.ValidateAnalyze(null, [Ok("a.pdf")])!.Value.JobMissing);
        Assert.True(RunFlow.ValidateAnalyze(Job(active: false), [Ok("a.pdf")])!.Value.JobMissing);
        Assert.Contains("Chưa có CV nào", RunFlow.ValidateAnalyze(Job(), [])!.Value.Message);

        var invalid = new UploadItem { FileName = "x.docx", State = UploadItemState.Invalid, Message = "Sai định dạng" };
        var rejected = new UploadItem { FileName = "dup.pdf", State = UploadItemState.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate };
        var unchecked_ = Ok("b.pdf");
        unchecked_.Selected = false;
        Assert.Contains("Chưa có CV hợp lệ nào được chọn", RunFlow.ValidateAnalyze(Job(), [invalid, rejected, unchecked_])!.Value.Message);

        var uploading = new UploadItem { FileName = "c.pdf", State = UploadItemState.Uploading };
        Assert.Contains("Đang tải CV lên", RunFlow.ValidateAnalyze(Job(), [Ok("a.pdf"), uploading])!.Value.Message);

        Assert.Null(RunFlow.ValidateAnalyze(Job(), [Ok("a.pdf"), invalid, rejected]));
    }

    [Fact]
    public void Only_uploaded_or_existing_selected_cvs_with_real_id_are_sent()
    {
        var existing = new UploadItem { FileName = "old.pdf", State = UploadItemState.Existing, CvId = Guid.NewGuid() };
        var noId = new UploadItem { FileName = "noid.pdf", State = UploadItemState.Uploaded };   // không có CvId
        var invalid = new UploadItem { FileName = "x.docx", State = UploadItemState.Invalid };
        var a = Ok("a.pdf");

        Assert.Equal([a, existing], RunFlow.SelectedForAnalysis([a, existing, noId, invalid]));
    }

    [Fact]
    public void Application_ids_skip_missing_cvs()
    {
        var response = new CreateApplicationsResponse
        {
            Items =
            [
                new() { Status = ApplicationItemStatuses.Created, ApplicationId = 14 },
                new() { Status = ApplicationItemStatuses.AlreadyExists, ApplicationId = 9 },
                new() { Status = ApplicationItemStatuses.CvNotFound },
                new() { Status = ApplicationItemStatuses.Created, ApplicationId = 14 },
            ]
        };

        var (ids, notFound) = RunFlow.ApplicationIdsFrom(response);
        Assert.Equal([14, 9], ids);
        Assert.Equal(1, notFound);
    }

    private static RunProgressDto Run(string status, int total = 2, int processed = 0, int failed = 0, decimal percent = 0, int id = 7,
        params RunItemDto[] items) =>
        new(id, RunTypes.Screening, status, 2, "v2", "gemini-2.5-flash", 0.2m, 0.3m, 0.7m, 70m, total, processed, failed, percent,
            new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc), null) { Items = [.. items] };

    [Theory]
    [InlineData(RunStatuses.Queued, 0, 0, RunOutcome.Queued)]
    [InlineData(RunStatuses.Running, 1, 0, RunOutcome.Running)]
    [InlineData(RunStatuses.Completed, 2, 0, RunOutcome.Completed)]
    [InlineData(RunStatuses.Completed, 2, 1, RunOutcome.PartialFailure)]
    [InlineData(RunStatuses.Completed, 2, 2, RunOutcome.AllFailed)]
    [InlineData(RunStatuses.Failed, 1, 1, RunOutcome.Failed)]
    public void Outcome_covers_all_run_states(string status, int processed, int failed, RunOutcome expected) =>
        Assert.Equal(expected, RunFlow.Outcome(Run(status, processed: processed, failed: failed)));

    [Fact]
    public void Item_errors_hide_technical_details()
    {
        RunItemDto Failed(string message) => new() { Status = EvaluationStatuses.Failed, ErrorMessage = message };

        Assert.Equal("CV chưa trích xuất được nội dung.",
            RunFlow.FriendlyItemError(Failed("CV chưa trích xuất được nội dung (ExtractionStatus = Failed).")));
        Assert.Equal("Lỗi khi chấm hồ sơ.", RunFlow.FriendlyItemError(Failed("Lỗi khi chấm hồ sơ (HttpRequestException).")));
        Assert.Equal("AI chưa chấm được, điểm chỉ tính theo từ khóa.", RunFlow.FriendlyItemError(new RunItemDto
        {
            Status = EvaluationStatuses.Completed, ScoreSource = ScoreSources.KeywordOnly, ErrorMessage = "Gemini 429 quota exceeded"
        }));
        Assert.Null(RunFlow.FriendlyItemError(new RunItemDto { Status = EvaluationStatuses.Completed, ScoreSource = ScoreSources.Combined }));
    }

    [Fact]
    public void Server_errors_use_generic_message()
    {
        Assert.Equal("chung", RunFlow.FailureMessage(ApiResult<int>.Failure("Cấu hình chấm điểm không thể lưu chính xác.", 500), "chung"));
        Assert.Equal("Cần đúng một phiên bản prompt đang hoạt động.",
            RunFlow.FailureMessage(ApiResult<int>.Failure("Cần đúng một phiên bản prompt đang hoạt động.", 409), "chung"));
    }

    // ================= (2) Trang Upload với API giả lập =================

    /// <summary>API giả: ghi lại mọi request; mỗi endpoint trả theo hàm do test đặt.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Applications { get; set; } = _ => throw new InvalidOperationException("Không mong gọi /api/applications");
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> StartRun { get; set; } = _ => throw new InvalidOperationException("Không mong gọi POST /api/runs");
        public Func<int, Task<HttpResponseMessage>> GetRun { get; set; } = _ => throw new InvalidOperationException("Không mong gọi GET /api/runs/{id}");
        public string? LastApplicationsBody { get; private set; }
        public string? LastStartRunBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add($"{request.Method} {path}");
            if (path == "/api/jobs")
                return Json(new[] { new JobDto { JobId = 1, Title = "Backend Developer (.NET)", IsActive = true } });
            if (request.Method == HttpMethod.Post && path == "/api/applications")
            {
                LastApplicationsBody = await request.Content!.ReadAsStringAsync(ct);
                return await Applications(request);
            }
            if (request.Method == HttpMethod.Post && path == "/api/runs")
            {
                LastStartRunBody = await request.Content!.ReadAsStringAsync(ct);
                return await StartRun(request);
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/runs/"))
                return await GetRun(int.Parse(path["/api/runs/".Length..]));
            throw new InvalidOperationException($"Request không mong đợi: {request.Method} {path}");
        }
    }

    private static HttpResponseMessage Json<T>(T body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    private static HttpResponseMessage Problem(int status, string title) =>
        new((HttpStatusCode)status) { Content = JsonContent.Create(new { title, status }) };

    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", isInteractive: true);
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        private int rootId;
        public Upload Page { get; private set; } = null!;

        public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
        {
            Page = (Upload)InstantiateComponent(typeof(Upload));
            rootId = AssignRootComponentId(Page);
            await RenderRootComponentAsync(rootId);
        });

        public List<RenderTreeFrame> Frames(int? id = null)
        {
            var frames = GetCurrentRenderTreeFrames(id ?? rootId);
            return frames.Array.Take(frames.Count).SelectMany(f => f.FrameType == RenderTreeFrameType.Component
                ? new[] { f }.Concat(Frames(f.ComponentId)) : [f]).ToList();
        }

        public string Text => Dispatcher.InvokeAsync(() => System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(string.Concat(
            Frames().Select(f => f.FrameType switch
            {
                RenderTreeFrameType.Text => f.TextContent,
                RenderTreeFrameType.Markup => System.Text.RegularExpressions.Regex.Replace(f.MarkupContent, "<[^>]+>", " "),
                _ => " "
            }))), @"\s+", " ")).Result;

        public List<UploadItem> Items => (List<UploadItem>)Frames()
            .First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "Items").AttributeValue;

        public Task ChangeJobAsync(string value) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Frames().First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onchange").AttributeEventHandlerId,
            null, new ChangeEventArgs { Value = value }));

        /// <summary>Bấm nút có chữ cho trước; trả Task của handler (để test bấm lặp khi request đang chạy).</summary>
        public Task<Task> ClickAsync(string text) => Dispatcher.InvokeAsync<Task>(() =>
        {
            var frames = Frames();
            var index = frames.FindIndex(f => (f.FrameType == RenderTreeFrameType.Text && f.TextContent.Contains(text))
                                              || (f.FrameType == RenderTreeFrameType.Markup && f.MarkupContent.Contains(text)));
            Assert.True(index >= 0, $"Không thấy nút \"{text}\"");
            var handler = frames.Take(index).Last(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick");
            return DispatchEventAsync(handler.AttributeEventHandlerId, null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        });

        private List<RenderTreeFrame> AnalyzeButtonAttributes()
        {
            var frames = Frames();
            var cls = frames.FindIndex(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "class"
                                            && f.AttributeValue is string s && s.Contains("sc-btn-analyze"));
            Assert.True(cls >= 0, "Không thấy nút Phân tích hồ sơ");
            var element = frames.Take(cls).ToList().FindLastIndex(f => f.FrameType == RenderTreeFrameType.Element);
            return frames.Skip(element + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToList();
        }

        public bool AnalyzeButtonDisabled => Dispatcher.InvokeAsync(() => AnalyzeButtonAttributes().Any(f => f.AttributeName == "disabled")).Result;

        /// <summary>Bấm nút "Phân tích hồ sơ" (gọi thẳng handler kể cả khi nút đang khóa, để kiểm tra chặn bấm lặp).</summary>
        public Task<Task> ClickAnalyzeAsync() => Dispatcher.InvokeAsync<Task>(() => DispatchEventAsync(
            AnalyzeButtonAttributes().First(f => f.AttributeName == "onclick").AttributeEventHandlerId,
            null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
    }

    private static ServiceProvider Services(FakeApi api, TestNavigationManager nav)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiBaseUrl"] = "https://api.test/",
            ["RunProgress:PollIntervalMs"] = "30"   // test: hỏi nhanh thay vì 2 giây
        }).Build());
        services.Configure<CvUploadOptions>(_ => { });
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>();
        services.AddSingleton<NavigationManager>(nav);
        services.AddSupplyValueFromQueryProvider();   // [SupplyParameterFromQuery] như ứng dụng thật
        var http = new HttpClient(api) { BaseAddress = new Uri("https://api.test/") };
        services.AddSingleton(new JobApiClient(http, NullLogger<JobApiClient>.Instance));
        services.AddSingleton(new CvApiClient(http, Microsoft.Extensions.Options.Options.Create(new CvUploadOptions()), NullLogger<CvApiClient>.Instance));
        services.AddSingleton(new ApplicationApiClient(http, NullLogger<ApplicationApiClient>.Instance));
        services.AddSingleton(new RunApiClient(http, NullLogger<RunApiClient>.Instance));
        return services.BuildServiceProvider();
    }

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    /// <summary>Chờ tới khi điều kiện đúng (vòng hỏi tiến độ chạy nền), tối đa 5 giây.</summary>
    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Hết thời gian chờ: {because}");
            await Task.Delay(10);
        }
    }

    private static async Task<TestRenderer> OpenAsync(ServiceProvider services)
    {
        var renderer = new TestRenderer(services);
        await renderer.StartAsync();
        return renderer;
    }

    private static Task AddItemsAsync(TestRenderer r, params UploadItem[] items) => r.Dispatcher.InvokeAsync(() => r.Items.AddRange(items));

    [Fact]
    public async Task Analyze_without_job_shows_error_and_calls_no_api()
    {
        var api = new FakeApi();
        using var services = Services(api, new TestNavigationManager());
        await using var r = await OpenAsync(services);
        await AddItemsAsync(r, Ok("a.pdf"));

        await await r.ClickAnalyzeAsync();

        Assert.Contains("Chưa chọn công việc", r.Text);
        Assert.Contains("Hãy chọn công việc trước khi phân tích.", r.Text);
        Assert.DoesNotContain(api.Calls, c => c.Contains("/api/applications") || c.Contains("/api/runs"));
    }

    [Fact]
    public async Task Analyze_with_only_invalid_cvs_from_B5_calls_no_api()
    {
        var api = new FakeApi();
        using var services = Services(api, new TestNavigationManager());
        await using var r = await OpenAsync(services);
        await r.ChangeJobAsync("1");
        await AddItemsAsync(r,
            new UploadItem { FileName = "cv-word.docx", State = UploadItemState.Invalid, Selected = false, Message = "Sai định dạng" },
            new UploadItem { FileName = "cv-trung.pdf", State = UploadItemState.Rejected, Selected = false, ErrorCode = CvUploadErrorCodes.Duplicate });

        await await r.ClickAnalyzeAsync();

        Assert.Contains("Chưa có CV hợp lệ nào được chọn", r.Text);
        Assert.DoesNotContain(api.Calls, c => c.Contains("/api/applications") || c.Contains("/api/runs"));
        Assert.Contains("cv-word.docx", r.Text);   // B5: dòng lỗi vẫn còn, không bị xóa
    }

    [Fact]
    public async Task Successful_analysis_sends_only_valid_cvs_navigates_and_polls_until_completed()
    {
        var api = new FakeApi();
        var nav = new TestNavigationManager();
        var good = Ok("a.pdf");
        var existing = new UploadItem { FileName = "cu.pdf", State = UploadItemState.Existing, CvId = Guid.NewGuid() };
        var bad = new UploadItem { FileName = "x.docx", State = UploadItemState.Invalid, Selected = false };

        var releaseApplications = new TaskCompletionSource();
        api.Applications = async _ =>
        {
            await releaseApplications.Task;   // giữ request để kiểm tra nút bị khóa và bấm lặp
            return Json(new CreateApplicationsResponse
            {
                JobId = 1,
                Items = [new() { CvId = good.CvId!.Value, ApplicationId = 14, Status = ApplicationItemStatuses.Created },
                         new() { CvId = existing.CvId!.Value, ApplicationId = 15, Status = ApplicationItemStatuses.AlreadyExists }]
            });
        };
        api.StartRun = _ => Task.FromResult(Json(Run(RunStatuses.Queued, id: 31), HttpStatusCode.Created));

        var polls = 0;
        api.GetRun = id =>
        {
            Assert.Equal(31, id);
            var n = Interlocked.Increment(ref polls);
            var item1 = new RunItemDto { EvaluationId = 1, ApplicationId = 14, FileName = "a.pdf", JobTitle = "Backend Developer (.NET)",
                Status = n >= 2 ? EvaluationStatuses.Completed : EvaluationStatuses.Pending, FinalScore = n >= 2 ? 82.5m : null,
                KeywordScore = n >= 2 ? 70 : null, AiScore = n >= 2 ? 88 : null, FitLabel = n >= 2 ? "Phù hợp" : null, ScoreSource = ScoreSources.Combined };
            var item2 = new RunItemDto { EvaluationId = 2, ApplicationId = 15, FileName = "cu.pdf", JobTitle = "Backend Developer (.NET)",
                Status = n >= 3 ? EvaluationStatuses.Failed : EvaluationStatuses.Pending,
                ErrorMessage = n >= 3 ? "CV chưa trích xuất được nội dung (ExtractionStatus = Failed)." : null };
            var dto = n switch
            {
                1 => Run(RunStatuses.Running, processed: 0, id: 31, items: [item1, item2]),
                2 => Run(RunStatuses.Running, processed: 1, percent: 50, id: 31, items: [item1, item2]),
                _ => Run(RunStatuses.Completed, processed: 2, failed: 1, percent: 100, id: 31, items: [item1, item2])
            };
            return Task.FromResult(Json(dto));
        };

        using var services = Services(api, nav);
        await using var r = await OpenAsync(services);
        await r.ChangeJobAsync("1");
        await AddItemsAsync(r, good, existing, bad);

        var first = await r.ClickAnalyzeAsync();
        Assert.Contains("Đang gắn CV vào công việc...", r.Text);
        Assert.True(r.AnalyzeButtonDisabled);

        // Bấm lặp khi đang chạy (gọi thẳng handler, bỏ qua thuộc tính disabled): không tạo request thứ hai
        await await r.ClickAnalyzeAsync();
        releaseApplications.SetResult();
        await first;

        Assert.Single(api.Calls, c => c == "POST /api/applications");
        Assert.Single(api.Calls, c => c == "POST /api/runs");
        Assert.Contains(good.CvId!.Value.ToString(), api.LastApplicationsBody);
        Assert.Contains(existing.CvId!.Value.ToString(), api.LastApplicationsBody);
        Assert.Contains("\"jobId\":1", api.LastApplicationsBody);
        Assert.Contains("[14,15]", api.LastStartRunBody);
        Assert.Equal("https://app.test/upload?runId=31", nav.Uri);

        await WaitUntil(() => r.Text.Contains("Đã xong: 1 hồ sơ chấm thành công, 1 hồ sơ lỗi"), "lượt chuyển sang Completed có lỗi");
        var text = r.Text;
        Assert.Contains("Lượt phân tích #31", text);
        Assert.Contains("Prompt v2", text);
        Assert.Contains("gemini-2.5-flash", text);
        Assert.Contains("Từ khóa 30% · AI 70%", text);
        Assert.Contains("100%", text);
        Assert.Contains("82.5", text);
        Assert.Contains("Phù hợp", text);
        Assert.Contains("CV chưa trích xuất được nội dung.", text);
        Assert.DoesNotContain("ExtractionStatus = Failed", text);
        Assert.Contains("Đã dừng cập nhật", text);
        // B7: lượt đã xong → liên kết "Xem bảng xếp hạng" mở đúng lượt vừa chấm
        Assert.Contains("Xem bảng xếp hạng", text);
        Assert.Contains("results?runId=31", r.Frames().Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "href")
            .Select(f => f.AttributeValue as string));

        // Đã Completed: ngừng hỏi API
        var after = polls;
        await Task.Delay(200);
        Assert.Equal(after, polls);
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task Application_api_failure_shows_message_and_does_not_create_run()
    {
        var api = new FakeApi { Applications = _ => Task.FromResult(Problem(404, "Không tìm thấy vị trí")) };
        using var services = Services(api, new TestNavigationManager());
        await using var r = await OpenAsync(services);
        await r.ChangeJobAsync("1");
        await AddItemsAsync(r, Ok("a.pdf"));

        await await r.ClickAnalyzeAsync();

        Assert.Contains("Không gắn được CV vào công việc", r.Text);
        Assert.Contains("Không tìm thấy vị trí", r.Text);
        Assert.DoesNotContain(api.Calls, c => c == "POST /api/runs");
        Assert.False(r.AnalyzeButtonDisabled);   // bấm lại được
    }

    [Fact]
    public async Task Run_api_failure_shows_api_reason_or_generic_server_error()
    {
        var api = new FakeApi
        {
            Applications = _ => Task.FromResult(Json(new CreateApplicationsResponse
            {
                Items = [new() { ApplicationId = 3, Status = ApplicationItemStatuses.Created }]
            })),
            StartRun = _ => Task.FromResult(Problem(409, "Cần đúng một phiên bản prompt đang hoạt động."))
        };
        var nav = new TestNavigationManager();
        using var services = Services(api, nav);
        await using var r = await OpenAsync(services);
        await r.ChangeJobAsync("1");
        await AddItemsAsync(r, Ok("a.pdf"));

        await await r.ClickAnalyzeAsync();
        Assert.Contains("Không tạo được lượt phân tích", r.Text);
        Assert.Contains("Cần đúng một phiên bản prompt đang hoạt động.", r.Text);
        Assert.Empty(nav.History);

        api.StartRun = _ => Task.FromResult(Problem(500, "Cấu hình chấm điểm không thể lưu chính xác."));
        await await r.ClickAnalyzeAsync();
        Assert.Contains("Máy chủ gặp lỗi khi tạo lượt phân tích", r.Text);
        Assert.DoesNotContain("Cấu hình chấm điểm", r.Text);
    }

    [Fact]
    public async Task Opening_url_with_runId_loads_that_run_queued_at_zero_percent()
    {
        var api = new FakeApi { GetRun = id => Task.FromResult(Json(Run(RunStatuses.Queued, total: 3, id: id))) };
        using var services = Services(api, new TestNavigationManager("https://app.test/upload?runId=4"));
        await using var r = await OpenAsync(services);

        await WaitUntil(() => r.Text.Contains("Lượt phân tích #4"), "tải lượt #4 từ địa chỉ");
        var text = r.Text;
        Assert.Contains("Đang chờ", text);
        Assert.Contains("0%", text);
        Assert.Contains("đang chờ máy chủ bắt đầu", text);
        Assert.Contains("Tự động cập nhật mỗi 2 giây", text);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("POST"));   // mở URL không tạo lượt mới
    }

    [Fact]
    public async Task Unknown_runId_shows_not_found_and_stops_polling()
    {
        var calls = 0;
        var api = new FakeApi { GetRun = _ => { Interlocked.Increment(ref calls); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); } };
        using var services = Services(api, new TestNavigationManager("https://app.test/upload?runId=999"));
        await using var r = await OpenAsync(services);

        await WaitUntil(() => r.Text.Contains("Không tìm thấy lượt phân tích #999"), "báo không tìm thấy");
        await Task.Delay(200);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Failed_run_and_lost_connection_are_reported()
    {
        var mode = "down";
        var api = new FakeApi
        {
            GetRun = id => mode == "down"
                ? throw new HttpRequestException("Connection refused 127.0.0.1:7269")
                : Task.FromResult(Json(Run(RunStatuses.Failed, processed: 1, failed: 1, percent: 50, id: id)))
        };
        using var services = Services(api, new TestNavigationManager("https://app.test/upload?runId=8"));
        await using var r = await OpenAsync(services);

        await WaitUntil(() => r.Text.Contains("Không tải được tiến độ"), "báo mất kết nối");
        Assert.Contains("Không kết nối được máy chủ API", r.Text);
        Assert.DoesNotContain("Connection refused", r.Text);

        mode = "up";   // API chạy lại: vòng hỏi tự thử lại và lấy được dữ liệu
        await WaitUntil(() => r.Text.Contains("Lượt phân tích bị dừng do lỗi phía máy chủ"), "hiện lượt Failed");
        Assert.Contains("Thất bại", r.Text);
        Assert.DoesNotContain("Không tải được tiến độ", r.Text);
    }

    [Fact]
    public async Task Changing_runId_or_leaving_page_stops_old_polling()
    {
        var calls = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var api = new FakeApi
        {
            GetRun = id => { calls.Enqueue(id); return Task.FromResult(Json(Run(RunStatuses.Running, processed: 1, percent: 50, id: id))); }
        };
        var nav = new TestNavigationManager("https://app.test/upload?runId=5");
        using var services = Services(api, nav);
        var r = await OpenAsync(services);

        await WaitUntil(() => calls.Count(x => x == 5) >= 2, "đang hỏi lượt 5");
        await r.Dispatcher.InvokeAsync(() => nav.NavigateTo("upload?runId=6"));
        await WaitUntil(() => r.Text.Contains("Lượt phân tích #6"), "chuyển sang lượt 6");
        var fiveCount = calls.Count(x => x == 5);
        await Task.Delay(200);
        Assert.Equal(fiveCount, calls.Count(x => x == 5));   // lượt 5 không còn bị hỏi
        Assert.DoesNotContain("Lượt phân tích #5", r.Text);

        await r.DisposeAsync();   // rời trang
        var total = calls.Count;
        await Task.Delay(200);
        Assert.Equal(total, calls.Count);
    }

    [Fact]
    public async Task Back_to_upload_without_runId_keeps_B3_list_and_B4_job()
    {
        var api = new FakeApi { GetRun = id => Task.FromResult(Json(Run(RunStatuses.Completed, processed: 2, percent: 100, id: id))) };
        var nav = new TestNavigationManager();
        using var services = Services(api, nav);
        await using var r = await OpenAsync(services);
        await r.ChangeJobAsync("1");
        var item = Ok("giu-lai.pdf");
        await AddItemsAsync(r, item);

        await r.Dispatcher.InvokeAsync(() => nav.NavigateTo("upload?runId=9"));
        await WaitUntil(() => r.Text.Contains("Lượt phân tích #9"), "mở lượt 9");
        await r.Dispatcher.InvokeAsync(() => nav.NavigateTo("upload"));

        Assert.Contains("giu-lai.pdf", r.Text);
        Assert.Contains("Kỹ năng bắt buộc:", r.Text);   // B4: job vẫn được chọn
        Assert.Same(item, Assert.Single(r.Items));
    }

    [Fact]
    public async Task Analyze_tab_without_run_shows_empty_state()
    {
        using var services = Services(new FakeApi(), new TestNavigationManager());
        await using var r = await OpenAsync(services);

        await await r.ClickAsync("Phân tích hồ sơ");   // chữ khớp đầu tiên là nút thẻ (đứng trước nút phân tích)
        Assert.Contains("Chưa có lượt phân tích nào", r.Text);
    }
}
#pragma warning restore BL0006
