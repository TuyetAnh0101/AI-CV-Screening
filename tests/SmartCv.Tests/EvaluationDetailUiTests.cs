extern alias web;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Components.Shared;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

#pragma warning disable BL0006
public sealed class EvaluationDetailUiTests
{
    private sealed class Api : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests.Add(request.RequestUri!.AbsolutePath); return Reply(request, ct); }
    }
    private sealed class Nav : NavigationManager
    {
        public Nav() => Initialize("https://web.test/", "https://web.test/results/evaluations/22?runId=3&jobId=1");
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).AbsoluteUri; NotifyLocationChanged(false); }
    }
    private sealed class TestRenderer : Renderer
    {
        public TestRenderer(IServiceProvider services) : base(services, NullLoggerFactory.Instance) { }
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", true);
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        private int root;
        public Task Start<T>(Dictionary<string, object?> parameters) where T : IComponent => Dispatcher.InvokeAsync(async () =>
        {
            root = AssignRootComponentId(InstantiateComponent(typeof(T)));
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(parameters));
        });
        public Task SetId(int id) => Dispatcher.InvokeAsync(() => RenderRootComponentAsync(root,
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(EvaluationDetail.Id)] = id })));
        private List<RenderTreeFrame> Flat(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            return frames.Array.Take(frames.Count).SelectMany(f => f.FrameType == RenderTreeFrameType.Component
                ? Flat(f.ComponentId) : [f]).ToList();
        }
        public Task<List<RenderTreeFrame>> Frames() => Dispatcher.InvokeAsync(() => Flat(root));
        public async Task<string> Text() => System.Text.RegularExpressions.Regex.Replace(string.Concat((await Frames()).Select(f => f.FrameType switch
        {
            RenderTreeFrameType.Text => f.TextContent,
            RenderTreeFrameType.Markup => System.Text.RegularExpressions.Regex.Replace(f.MarkupContent, "<[^>]+>", " "),
            _ => " "
        })), @"\s+", " ");
        public Task ClickCv() => Dispatcher.InvokeAsync(() =>
        {
            var handler = Flat(root).First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick");
            return DispatchEventAsync(handler.AttributeEventHandlerId, null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        });
    }
    private static EvaluationDetailDto Detail(int id = 22, string source = "Combined", string status = "Completed", string? reason = "Có kinh nghiệm C#.") => new()
    {
        EvaluationId = id, CandidateName = $"Ứng viên {id}", CandidateEmail = "candidate@example.test", FileName = "CV.pdf", JobTitle = ".NET Developer",
        CvId = Guid.Parse("11111111-1111-1111-1111-111111111111"), RunId = 3, JobId = 1, Rank = 2, RankTotal = 3,
        Status = status, ScoreSource = source, KeywordScore = 80, AiScore = 60, FinalScore = 66,
        KeywordWeight = .3m, AiWeight = .7m, Reasoning = reason, AiRawResponse = "{\"reasoning\":\"<script>alert(1)</script>\"}",
        CreatedAt = new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc),
        Skills = [new() { DisplayName = "C#", SkillType = "MustHave", IsMatched = true, Evidence = "[Gemini xác nhận, không phải trích dẫn từ CV]" },
            new() { DisplayName = "SQL", SkillType = "MustHave", IsMatched = false }]
    };
    private static ServiceProvider Services(Api api)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<NavigationManager>(new Nav());
        services.AddSupplyValueFromQueryProvider();
        services.AddSingleton<Microsoft.AspNetCore.Components.Web.IErrorBoundaryLogger, BoundaryLogger>();
        services.AddSingleton(new EvaluationApiClient(new HttpClient(api) { BaseAddress = new Uri("https://api.test:7123/") }, NullLogger<EvaluationApiClient>.Instance));
        return services.BuildServiceProvider();
    }
    private sealed class BoundaryLogger : Microsoft.AspNetCore.Components.Web.IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }
    private static HttpResponseMessage Json(EvaluationDetailDto d) => new(HttpStatusCode.OK) { Content = JsonContent.Create(d) };

    [Fact]
    public void Route_query_backlink_and_vietnam_time_are_correct()
    {
        Assert.Equal("/results/evaluations/{Id:int}", typeof(EvaluationDetail).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("runId", typeof(EvaluationDetail).GetProperty("RunId")!.GetCustomAttribute<SupplyParameterFromQueryAttribute>()!.Name);
        Assert.Equal("/results?runId=3&jobId=1", Display.BackUrl("3", "1"));
        Assert.Equal("/results?jobId=1", Display.BackUrl(null, "1"));
        Assert.Equal("/results", Display.BackUrl(null, null));
        var utc = new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc);
        Assert.Equal("10/11/2026 09:00 UTC+7", Display.VnTime(utc));
        Assert.Equal(Display.VnTime(utc), Display.VnTime(utc.ToLocalTime()));
        Assert.Equal(Display.VnTime(utc), Display.VnTime(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)));
        Assert.Equal("—", Display.VnTime(default));
    }

    [Fact]
    public async Task Page_uses_real_dto_scores_evidence_raw_text_and_preserves_query()
    {
        var api = new Api { Reply = (_, _) => Task.FromResult(Json(Detail())) };
        using var services = Services(api);
        await using var renderer = new TestRenderer(services);
        await renderer.Start<EvaluationDetail>(new() { ["Id"] = 22 });
        var text = await renderer.Text();
        Assert.Contains("Ứng viên 22", text);
        Assert.Contains("80 × 30% = 24", text);
        Assert.Contains("60 × 70% = 42", text);
        Assert.Contains("Có kinh nghiệm C#.", text);
        Assert.Contains("[Gemini xác nhận, không phải trích dẫn từ CV]", text);
        Assert.Contains("Không tìm thấy trong CV", text);
        var frames = await renderer.Frames();
        Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Text && f.TextContent.Contains("<script>"));
        Assert.DoesNotContain(frames, f => f.FrameType == RenderTreeFrameType.Markup && f.MarkupContent.Contains("<script>"));
        Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "href" && Equals(f.AttributeValue, "/results?runId=3&jobId=1"));
        await renderer.SetId(22);
        Assert.Single(api.Requests);
        await renderer.SetId(23);
        Assert.Equal(2, api.Requests.Count);
        Assert.Contains("Dữ liệu đánh giá trả về không hợp lệ", await renderer.Text());
    }

    [Theory]
    [InlineData("KeywordOnly", "Completed", "AI chưa chấm thành công")]
    [InlineData("Combined", "Pending", "Chưa có kết quả hoàn tất")]
    [InlineData("Combined", "Failed", "Không chấm được CV này")]
    public async Task Unavailable_ai_is_never_presented_as_success(string source, string status, string expected)
    {
        var api = new Api { Reply = (_, _) => Task.FromResult(Json(Detail(source: source, status: status, reason: "SHOULD_NOT_BE_AI_REVIEW"))) };
        using var services = Services(api);
        await using var renderer = new TestRenderer(services);
        await renderer.Start<EvaluationDetail>(new() { ["Id"] = 22 });
        var text = await renderer.Text();
        Assert.Contains(expected, text);
        Assert.DoesNotContain("SHOULD_NOT_BE_AI_REVIEW", text);
        Assert.DoesNotContain("60 × 70% = 42", text);
    }

    [Theory]
    [InlineData(404, "Không tìm thấy kết quả chấm #9999.")]
    [InlineData(401, "đăng nhập lại")]
    [InlineData(403, "không có quyền")]
    [InlineData(500, "Vui lòng thử lại sau")]
    public async Task Http_errors_are_safe(int status, string expected)
    {
        var api = new Api { Reply = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = JsonContent.Create(new { detail = "SECRET_TECHNICAL_TRACE" }) }) };
        using var services = Services(api);
        await using var renderer = new TestRenderer(services);
        await renderer.Start<EvaluationDetail>(new() { ["Id"] = 9999 });
        Assert.Contains(expected, await renderer.Text());
        Assert.DoesNotContain("SECRET", await renderer.Text());
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("offline")]
    [InlineData("timeout")]
    [InlineData("empty")]
    public async Task Invalid_or_unavailable_api_does_not_crash(string mode)
    {
        var api = new Api { Reply = (_, _) => mode switch
        {
            "offline" => throw new HttpRequestException("private path"),
            "timeout" => throw new TaskCanceledException("private path"),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(mode == "empty" ? "null" : "broken json") })
        } };
        using var services = Services(api);
        await using var renderer = new TestRenderer(services);
        await renderer.Start<EvaluationDetail>(new() { ["Id"] = 22 });
        Assert.Contains("Không tải được", await renderer.Text());
        Assert.DoesNotContain("private path", await renderer.Text());
    }

    [Fact]
    public async Task Late_old_request_cannot_replace_new_id()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Api { Reply = (r, _) => r.RequestUri!.AbsolutePath.EndsWith("/22") ? old.Task : Task.FromResult(Json(Detail(23))) };
        using var services = Services(api);
        await using var renderer = new TestRenderer(services);
        var first = renderer.Start<EvaluationDetail>(new() { ["Id"] = 22 });
        await renderer.Dispatcher.InvokeAsync(() => { });
        Assert.Contains("Đang tải", await renderer.Text());
        var second = renderer.SetId(23);
        await renderer.Dispatcher.InvokeAsync(() => { });
        old.SetResult(Json(Detail(22)));
        await Task.WhenAll(first, second);
        Assert.Contains("Ứng viên 23", await renderer.Text());
        Assert.DoesNotContain("Ứng viên 22", await renderer.Text());
    }

    [Fact]
    public async Task Leaving_page_cancels_the_in_flight_request()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Api { Reply = async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, token);
            return Json(Detail());
        } };
        using var services = Services(api);
        var renderer = new TestRenderer(services);
        var loading = renderer.Start<EvaluationDetail>(new() { ["Id"] = 22 });
        await renderer.Dispatcher.InvokeAsync(() => { });
        await renderer.DisposeAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await loading;
    }

    [Fact]
    public async Task Cv_connection_failure_is_reported_without_exposing_provider_details()
    {
        var api = new Api { Reply = (r, _) => r.RequestUri!.AbsolutePath.Contains("evaluations")
            ? Task.FromResult(Json(Detail())) : throw new HttpRequestException("PRIVATE_SERVER_PATH") };
        using var services = Services(api);
        await using var renderer = new TestRenderer(services);
        await renderer.Start<EvaluationDetail>(new() { ["Id"] = 22 });
        await renderer.ClickCv();
        Assert.Contains("Không mở được CV", await renderer.Text());
        Assert.DoesNotContain("PRIVATE_SERVER_PATH", await renderer.Text());
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(0, 0, "0 100")]
    [InlineData(100, 100, "100 100")]
    [InlineData(120, -5, "100 100")]
    public async Task Ring_handles_null_zero_limits_and_accessibility(int? final, int? keyword, string? expected)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new TestRenderer(services);
        await renderer.Start<ScoreRing>(new() { ["Final"] = (decimal?)final, ["Keyword"] = (decimal?)keyword });
        var frames = await renderer.Frames();
        var dashes = frames.Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "stroke-dasharray").ToList();
        if (expected is null) Assert.Empty(dashes);
        else Assert.Equal(expected, dashes[0].AttributeValue);
        Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-label");
        Assert.Contains(final?.ToString() ?? "—", await renderer.Text());
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Failed")]
    public async Task Ring_hides_scores_for_incomplete_evaluation(string status)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new TestRenderer(services);
        await renderer.Start<ScoreRing>(new() { ["Final"] = 80m, ["Keyword"] = 90m, ["Status"] = status });
        Assert.DoesNotContain(await renderer.Frames(), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "stroke-dasharray");
        Assert.Contains("—", await renderer.Text());
    }

    [Fact]
    public void Dynamic_weights_and_cap_require_explicit_backend_evidence()
    {
        Assert.Equal(66, Display.WeightedTotal(Detail()));
        Assert.Equal(80, Display.WeightedTotal(Detail(source: "KeywordOnly")));
        Assert.Equal(75, Display.WeightedTotal(new() { Status = "Completed", ScoreSource = "Combined", KeywordScore = 80, AiScore = 60, KeywordWeight = .75m, AiWeight = .25m }));
        Assert.Null(Display.WeightedTotal(new() { Status = "Completed", ScoreSource = "Combined", KeywordScore = 80, AiScore = null, KeywordWeight = .3m, AiWeight = .7m }));
        Assert.False(Display.HasRecordedCap(Detail()));
        var capped = new EvaluationDetailDto { Status = "Completed", ScoreSource = "Combined", FinalScore = 65, KeywordScore = 80, AiScore = 60,
            KeywordWeight = .3m, AiWeight = .7m, Reasoning = "Nhận xét (Điểm cuối giới hạn 65 vì thiếu kỹ năng bắt buộc: SQL.)",
            Skills = [new() { SkillType = "MustHave", DisplayName = "SQL", IsMatched = false }] };
        Assert.True(Display.HasRecordedCap(capped));
        Assert.DoesNotContain("secret", Display.SafeError("https://provider/?key=secret"));
        Assert.Equal("Gemini báo HTTP 503. AI chưa chấm thành công.", Display.SafeError("Gemini lỗi sau 3 lần thử (HTTP 503: https://private/?key=secret)"));
    }

    [Theory]
    [InlineData(200, "Mở CV gốc")]
    [InlineData(404, "Không tìm thấy file CV gốc")]
    [InlineData(401, "đăng nhập lại")]
    [InlineData(403, "không có quyền xem CV")]
    public async Task Cv_uses_exact_id_and_api_origin_with_safe_errors(int status, string expected)
    {
        var api = new Api { Reply = (r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.Contains("evaluations") ? Json(Detail()) : new HttpResponseMessage((HttpStatusCode)status)) };
        using var services = Services(api);
        await using var renderer = new TestRenderer(services);
        await renderer.Start<EvaluationDetail>(new() { ["Id"] = 22 });
        await renderer.ClickCv();
        Assert.Contains(expected, await renderer.Text());
        Assert.Equal("/api/cvs/11111111-1111-1111-1111-111111111111/file", api.Requests.Last());
        if (status == 200) Assert.Contains(await renderer.Frames(), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "href"
            && Equals(f.AttributeValue, "https://api.test:7123/api/cvs/11111111-1111-1111-1111-111111111111/file"));
    }
}
#pragma warning restore BL0006
