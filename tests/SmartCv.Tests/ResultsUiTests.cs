extern alias web;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B7: trang Kết quả &amp; xếp hạng (/results). Phần (1) kiểm tra logic thuần (tìm không dấu, lọc, sắp xếp);
/// phần (2) dựng trang thật với API A13 GIẢ LẬP bằng HttpMessageHandler (dữ liệu chỉ nằm trong bộ nhớ test, không ghi database).
/// </summary>
#pragma warning disable BL0006 // RenderTree APIs chỉ dùng trong renderer kiểm thử.
public class ResultsUiTests
{
    // ================= (1) Logic thuần: RankingView =================

    [Theory]
    [InlineData("Trần Thị Bình", "tran thi binh")]
    [InlineData("ĐẶNG  Văn   Đức", "dang van duc")]
    [InlineData("CV_LeHoangCuong.pdf", "cv_lehoangcuong.pdf")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Fold_removes_vietnamese_marks_and_case(string? input, string expected) =>
        Assert.Equal(expected, RankingView.Fold(input));

    [Theory]
    [InlineData("binh")]
    [InlineData("BÌNH")]
    [InlineData("bình trần")]
    [InlineData("tranthibinh")]   // khớp tên file CV_TranThiBinh.pdf
    public void Search_matches_name_or_file_without_case_and_marks(string query) =>
        Assert.True(RankingView.MatchesSearch(Item(2, "Trần Thị Bình", "CV_TranThiBinh.pdf", 66), query));

    [Fact]
    public void Search_does_not_match_other_candidates() =>
        Assert.False(RankingView.MatchesSearch(Item(1, "Nguyễn Văn An", "CV_NguyenVanAn.pdf", 93), "binh"));

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("3", true, 3)]
    [InlineData(" 12 ", true, 12)]
    [InlineData("0", false, null)]
    [InlineData("-1", false, null)]
    [InlineData("abc", false, null)]
    [InlineData("1.5", false, null)]
    [InlineData("99999999999", false, null)]
    public void Query_ids_must_be_positive_integers(string? raw, bool ok, int? expected)
    {
        Assert.Equal(ok, RankingView.TryParseId(raw, out var id));
        Assert.Equal(expected, id);
    }

    [Fact]
    public void Every_sort_keeps_unscored_cvs_last()
    {
        RankingItemDto[] items =
        [
            Unscored(10, "Lý Văn Minh", EvaluationStatuses.Failed),
            Item(3, "Lê Hoàng Cường", "c.pdf", 24, ai: 30, kw: 10, years: 0),
            Unscored(11, "Phạm Pending", EvaluationStatuses.Pending),
            Item(1, "Nguyễn Văn An", "a.pdf", 93, ai: 90, kw: 100, years: 1.5m),
            Item(2, "Trần Thị Bình", "b.pdf", 66, ai: null, kw: 80, years: 0.5m, keywordOnly: true),
        ];

        foreach (var sort in Enum.GetValues<RankingSort>())
        {
            var sorted = RankingView.Sort(items, sort).ToList();
            Assert.All(sorted.Take(3), i => Assert.True(RankingView.IsScored(i), $"{sort}: CV có điểm phải đứng trước"));
            Assert.All(sorted.Skip(3), i => Assert.False(RankingView.IsScored(i), $"{sort}: CV chưa có điểm phải đứng cuối"));
        }

        Assert.Equal(["Nguyễn Văn An", "Trần Thị Bình", "Lê Hoàng Cường"],
            RankingView.Sort(items, RankingSort.FinalDesc).Take(3).Select(i => i.CandidateName));
        Assert.Equal(["Lê Hoàng Cường", "Trần Thị Bình", "Nguyễn Văn An"],
            RankingView.Sort(items, RankingSort.FinalAsc).Take(3).Select(i => i.CandidateName));
        // CV "chỉ keyword" (không có điểm AI) đứng sau CV có điểm AI thật
        Assert.Equal(["Nguyễn Văn An", "Lê Hoàng Cường", "Trần Thị Bình"],
            RankingView.Sort(items, RankingSort.AiDesc).Take(3).Select(i => i.CandidateName));
        Assert.Equal(["Nguyễn Văn An", "Trần Thị Bình", "Lê Hoàng Cường"],
            RankingView.Sort(items, RankingSort.ExperienceDesc).Take(3).Select(i => i.CandidateName));
        Assert.Equal(["Lê Hoàng Cường", "Nguyễn Văn An", "Trần Thị Bình"],
            RankingView.Sort(items, RankingSort.NameAsc).Take(3).Select(i => i.CandidateName));
    }

    [Fact]
    public void Filter_counts_equal_rows_shown()
    {
        var items = BackendRanking().Items;
        foreach (var (filter, _) in RankingView.Filters)
        {
            foreach (var query in new[] { "", "binh", "cv_" })
                Assert.Equal(RankingView.Count(items, query, filter), RankingView.Apply(items, query, filter, RankingSort.FinalDesc).Count);
        }
        Assert.Equal(4, RankingView.Count(items, "", RankingFilter.All));
        Assert.Equal(1, RankingView.Count(items, "", RankingFilter.Pass));
        Assert.Equal(2, RankingView.Count(items, "", RankingFilter.NotPass));
        Assert.Equal(1, RankingView.Count(items, "", RankingFilter.Unscored));
    }

    [Fact]
    public void Missing_scores_are_shown_as_dash_never_zero()
    {
        Assert.Equal("—", RankingView.Score(null));
        Assert.Equal("0", RankingView.Score(0));
        Assert.Equal("91,6", RankingView.Score(91.6m));
        Assert.Equal("—", RankingView.RankText(null));
        Assert.Equal("01", RankingView.RankText(1));

        var summary = RankingView.Summarize(BackendRanking());
        Assert.Equal((4, 3, 1), (summary.Total, summary.Scored, summary.Unscored));
        Assert.Equal(61m, summary.Average);   // (93 + 66 + 24) / 3: CV lỗi KHÔNG bị tính là 0
        Assert.Equal("Nguyễn Văn An", summary.Top!.CandidateName);
    }

    [Fact]
    public void Times_are_shown_in_vietnam_time() =>
        Assert.Equal("05/11/2026 09:00", RankingView.VnTime(new DateTime(2026, 11, 5, 2, 0, 0, DateTimeKind.Utc)));

    // ================= Dữ liệu giả lập API A13 =================

    private static RankingItemDto Item(int rank, string name, string file, decimal final, decimal? ai = 70, decimal? kw = 70,
        decimal? years = 1, bool keywordOnly = false, int id = 0) => new()
    {
        Rank = rank, EvaluationId = id == 0 ? 100 + rank : id, ApplicationId = rank, CvId = Guid.NewGuid(), FileName = file,
        CandidateName = name, FinalScore = final, AiScore = ai, KeywordScore = kw, YearsExperience = years,
        ScoreSource = keywordOnly ? ScoreSources.KeywordOnly : ScoreSources.Combined, Status = EvaluationStatuses.Completed,
        FitLabel = FitLevel.FromScore(final), PredictedPass = final >= 70, MatchedMustHave = 2, TotalMustHave = 3
    };

    private static RankingItemDto Unscored(int id, string name, string status) => new()
    {
        EvaluationId = id, ApplicationId = id, CvId = Guid.NewGuid(), FileName = $"CV_{id}.pdf", CandidateName = name,
        Status = status, TotalMustHave = 3,
        ErrorMessage = status == EvaluationStatuses.Failed ? "CV chưa trích xuất được nội dung (ExtractionStatus = Failed)." : null
    };

    private static readonly List<JobCountDto> Run3Jobs =
    [
        new() { JobId = 1, Title = "Backend Developer (.NET)", Count = 4 },
        new() { JobId = 2, Title = "Business Analyst", Count = 2 }
    ];

    /// <summary>Thứ tự trả về CỐ Ý xáo trộn để kiểm tra giao diện tự sắp xếp điểm cuối giảm dần.</summary>
    private static RankingResponse BackendRanking() => new()
    {
        RunId = 3, RunStatus = RunStatuses.Completed, PromptVersionName = "v2-tieu-chi-ro-rang", PassThreshold = 70,
        JobId = 1, JobTitle = "Backend Developer (.NET)", Jobs = Run3Jobs, PassCount = 1,
        Items =
        [
            Item(2, "Trần Thị Bình", "CV_TranThiBinh.pdf", 66, ai: 60, kw: 80, years: 0.5m),
            Unscored(14, "Lý Văn Minh", EvaluationStatuses.Failed),
            Item(1, "Nguyễn Văn An", "CV_NguyenVanAn.pdf", 93, ai: 90, kw: 100, years: 1.5m),
            Item(3, "Lê Hoàng Cường", "CV_LeHoangCuong.pdf", 24, ai: 30, kw: 10, years: 0),
        ]
    };

    private static RankingResponse AnalystRanking() => new()
    {
        RunId = 3, RunStatus = RunStatuses.Completed, PromptVersionName = "v2-tieu-chi-ro-rang", PassThreshold = 70,
        JobId = 2, JobTitle = "Business Analyst", Jobs = Run3Jobs, PassCount = 1,
        Items =
        [
            Item(1, "Dương Mỹ Linh", "CV_DuongMyLinh.pdf", 72, ai: null, kw: 72, keywordOnly: true, id: 201),
            Item(2, "Hoàng Thị Em", "CV_HoangThiEm.pdf", 58, id: 202),
        ]
    };

    private static RankingResponse RunningRanking() => new()
    {
        RunId = 4, RunStatus = RunStatuses.Running, PromptVersionName = "v2-tieu-chi-ro-rang", PassThreshold = 70,
        JobId = 3, JobTitle = "AI Engineer Intern", Jobs = [new() { JobId = 3, Title = "AI Engineer Intern", Count = 2 }], PassCount = 1,
        Items = [Item(1, "Võ Thành Nam", "CV_VoThanhNam.pdf", 81, id: 301), Unscored(302, "Đỗ Chưa Chấm", EvaluationStatuses.Pending)]
    };

    private static RunSummaryDto Summary(int id, string status, int total, int processed, List<JobCountDto> jobs, int day) => new()
    {
        RunId = id, RunType = RunTypes.Screening, Status = status, PromptVersionName = "v2-tieu-chi-ro-rang",
        ModelName = "gemini-2.5-flash", TotalItems = total, ProcessedItems = processed,
        CreatedAt = new DateTime(2026, 11, day, 2, 0, 0, DateTimeKind.Utc), Jobs = jobs
    };

    /// <summary>API A13 giả: ghi lại mọi request (đường dẫn + query); có thể chặn/trì hoãn từng request.</summary>
    private sealed class FakeResultsApi : HttpMessageHandler
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public List<RunSummaryDto> Runs { get; } =
        [
            Summary(4, RunStatuses.Running, 3, 1, [new() { JobId = 3, Title = "AI Engineer Intern", Count = 2 }], 6),
            Summary(3, RunStatuses.Completed, 6, 6, Run3Jobs, 5)
        ];
        public Dictionary<(int Run, int? Job), RankingResponse> Rankings { get; } = new()
        {
            [(3, null)] = BackendRanking(), [(3, 1)] = BackendRanking(), [(3, 2)] = AnalystRanking(),
            [(4, null)] = RunningRanking(), [(4, 3)] = RunningRanking()
        };
        public Dictionary<int, string> RunTypesById { get; } = new() { [3] = RunTypes.Screening, [4] = RunTypes.Screening };

        /// <summary>Nếu đặt: request được giữ tới khi Task hoàn tất (giả lập API chậm).</summary>
        public Func<string, Task>? Gate { get; set; }
        public bool Offline { get; set; }
        public int? ForceStatus { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            Calls.Enqueue(pathAndQuery);
            // Máy chủ trả lời muộn, kể cả khi trang đã đổi lựa chọn (giới hạn 10 giây để test không bao giờ treo)
            if (Gate is not null) await Gate(pathAndQuery).WaitAsync(TimeSpan.FromSeconds(10));
            if (Offline) throw new HttpRequestException("Connection refused");
            if (ForceStatus is { } status)
                return new((HttpStatusCode)status) { Content = JsonContent.Create(new { title = "Lỗi", detail = "SqlException: Timeout expired", status }) };

            var path = request.RequestUri.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);

            if (path == "/api/runs")
            {
                Assert.Equal(RunTypes.Screening, query["type"]);   // trang chỉ liệt kê lượt sàng lọc
                return Json(Runs);
            }
            if (path == "/api/runs/latest")
                return Runs.Count == 0 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(Runs[0]);

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);   // api, runs, {id}, [ranking]
            var runId = int.Parse(parts[2]);
            if (parts.Length == 4 && parts[3] == "ranking")
            {
                int? jobId = query["jobId"] is { } j ? int.Parse(j) : null;
                if (Rankings.TryGetValue((runId, jobId), out var ranking)) return Json(ranking);
                var title = Rankings.Keys.Any(k => k.Run == runId) ? "Job này không có trong lượt chấm" : "Không tìm thấy lượt chấm";
                return Problem(404, title);
            }
            if (parts.Length == 3 && RunTypesById.TryGetValue(runId, out var type))
                return Json(new RunProgressDto(runId, type, RunStatuses.Completed, 1, "v1", "gemini-2.5-flash", 0.2m, 0.3m, 0.7m, 70m,
                    10, 10, 0, 100, new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc), null));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        private static HttpResponseMessage Problem(int status, string title) =>
            new((HttpStatusCode)status) { Content = JsonContent.Create(new { title, status }) };
    }

    // ================= Renderer kiểm thử =================

    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", isInteractive: true);
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        private int rootId;

        /// <summary>
        /// Dựng trang. waitForLoad = false: không chờ trang tải xong (RenderRootComponentAsync chờ mọi tác vụ của trang),
        /// dùng khi test cố ý giữ một request của API.
        /// </summary>
        public Task StartAsync(bool waitForLoad = true) => Dispatcher.InvokeAsync(async () =>
        {
            var page = (Results)InstantiateComponent(typeof(Results));
            rootId = AssignRootComponentId(page);
            var render = RenderRootComponentAsync(rootId);
            if (waitForLoad) await render;
        });

        /// <summary>Khung render của từng component (trang + component con), theo thứ tự xuất hiện.</summary>
        private List<RenderTreeFrame[]> Components(int id)
        {
            var range = GetCurrentRenderTreeFrames(id);
            var frames = range.Array.Take(range.Count).ToArray();
            var all = new List<RenderTreeFrame[]> { frames };
            foreach (var f in frames.Where(f => f.FrameType == RenderTreeFrameType.Component))
                all.AddRange(Components(f.ComponentId));
            return all;
        }

        private List<RenderTreeFrame> Flat(int id)
        {
            var range = GetCurrentRenderTreeFrames(id);
            return range.Array.Take(range.Count).SelectMany(f => f.FrameType == RenderTreeFrameType.Component
                ? new[] { f }.Concat(Flat(f.ComponentId)) : [f]).ToList();
        }

        private static string Clean(IEnumerable<RenderTreeFrame> frames) =>
            System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(string.Concat(frames.Select(f => f.FrameType switch
            {
                RenderTreeFrameType.Text => f.TextContent,
                RenderTreeFrameType.Markup => System.Text.RegularExpressions.Regex.Replace(f.MarkupContent, "<[^>]+>", " "),
                _ => " "
            }))), @"\s+", " ").Trim();

        public string Text => Dispatcher.InvokeAsync(() => Clean(Flat(rootId))).Result;

        /// <summary>Chỉ phần bảng xếp hạng (bỏ đầu trang và thẻ số liệu, nơi tên người điểm cao nhất cũng xuất hiện).</summary>
        public string TableText
        {
            get
            {
                var text = Text;
                var start = text.IndexOf("Bảng xếp hạng ứng viên Mặc định", StringComparison.Ordinal);
                return start < 0 ? string.Empty : text[start..];
            }
        }

        /// <summary>Mọi href trên trang: thuộc tính động, và cả HTML tĩnh mà Razor gộp thành khung Markup.</summary>
        public List<string> Hrefs => Dispatcher.InvokeAsync(() => Flat(rootId).SelectMany(f => f.FrameType switch
        {
            RenderTreeFrameType.Attribute when f.AttributeName == "href" => [f.AttributeValue as string ?? string.Empty],
            RenderTreeFrameType.Markup => System.Text.RegularExpressions.Regex.Matches(f.MarkupContent, "href=\"([^\"]*)\"")
                .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)),
            _ => []
        }).ToList()).Result;

        /// <summary>Đổi địa chỉ như trình duyệt (gõ URL, Back/Forward): phải chạy trên Dispatcher của renderer.</summary>
        public Task NavigateAsync(NavigationManager nav, string url) => Dispatcher.InvokeAsync(() => nav.NavigateTo(url));

        private static string? Attr(RenderTreeFrame[] frames, int element, string name)
        {
            for (var j = element + 1; j < frames.Length && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
                if (frames[j].AttributeName == name) return frames[j].AttributeValue?.ToString();
            return null;
        }

        private static string Inner(RenderTreeFrame[] frames, int element) =>
            Clean(frames.Skip(element + 1).Take(frames[element].ElementSubtreeLength - 1));

        private ulong Handler(Func<RenderTreeFrame[], int, bool> match, string eventName)
        {
            foreach (var frames in Components(rootId))
                for (var i = 0; i < frames.Length; i++)
                {
                    if (frames[i].FrameType != RenderTreeFrameType.Element || !match(frames, i)) continue;
                    for (var j = i + 1; j < frames.Length && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
                        if (frames[j].AttributeName == eventName) return frames[j].AttributeEventHandlerId;
                }
            throw new Xunit.Sdk.XunitException($"Không thấy phần tử có sự kiện {eventName}");
        }

        /// <summary>Ô chọn thứ n trên trang: 0 = Lượt chấm, 1 = Vị trí tuyển dụng, 2 = Sắp xếp.</summary>
        public Task SelectAsync(int index, string value) => Dispatcher.InvokeAsync(() =>
        {
            var seen = -1;
            return DispatchEventAsync(Handler((f, i) => f[i].ElementName == "select" && ++seen == index, "onchange"),
                null, new ChangeEventArgs { Value = value });
        });

        public Task SearchAsync(string text) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Handler((f, i) => f[i].ElementName == "input" && Attr(f, i, "type") == "search", "oninput"),
            null, new ChangeEventArgs { Value = text }));

        public Task ClickButtonAsync(string text, string? cssClass = null) => Dispatcher.InvokeAsync(() => DispatchEventAsync(
            Handler((f, i) => f[i].ElementName == "button" && Inner(f, i).Contains(text)
                              && (cssClass is null || (Attr(f, i, "class") ?? "").Contains(cssClass)), "onclick"),
            null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
    }

    private static ServiceProvider Services(FakeResultsApi api, TestNavigationManager nav)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(nav);
        services.AddSupplyValueFromQueryProvider();   // [SupplyParameterFromQuery] như ứng dụng thật
        var http = new HttpClient(api) { BaseAddress = new Uri("https://api.test/") };
        services.AddSingleton(new RunApiClient(http, NullLogger<RunApiClient>.Instance));
        return services.BuildServiceProvider();
    }

    private static async Task<(TestRenderer Renderer, ServiceProvider Services)> OpenAsync(FakeResultsApi api, TestNavigationManager nav,
        bool waitForLoad = true)
    {
        var services = Services(api, nav);
        var renderer = new TestRenderer(services);
        await renderer.StartAsync(waitForLoad);
        return (renderer, services);
    }

    private static TestNavigationManager Nav(string relative = "results") => new($"https://app.test/{relative}");

    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Hết thời gian chờ: {because}");
            await Task.Delay(10);
        }
    }

    private static void AssertOrder(string text, params string[] names)
    {
        var positions = names.Select(n => text.IndexOf(n, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
    }

    // ================= (2) Trang /results với API giả lập =================

    [Fact]
    public async Task No_runs_shows_empty_state_with_link_to_upload()
    {
        var api = new FakeResultsApi();
        api.Runs.Clear();
        var (r, sp) = await OpenAsync(api, Nav());
        await using var _ = r; using var __ = sp;

        await WaitUntil(() => r.Text.Contains("Chưa có lượt chấm nào"), "trạng thái trống");
        Assert.Contains("upload", r.Hrefs);
        Assert.DoesNotContain(api.Calls, c => c.Contains("/ranking"));
        Assert.Equal(string.Empty, r.TableText);   // không có bảng xếp hạng
    }

    [Fact]
    public async Task Without_query_opens_latest_screening_run_and_lists_runs()
    {
        var api = new FakeResultsApi();
        var (r, sp) = await OpenAsync(api, Nav());
        await using var _ = r; using var __ = sp;

        await WaitUntil(() => r.Text.Contains("Võ Thành Nam"), "bảng xếp hạng lượt mới nhất");
        var text = r.Text;
        Assert.Contains("api/runs/latest", string.Join(' ', api.Calls));
        Assert.Contains("/api/runs?type=Screening&take=50", api.Calls);
        Assert.Contains("/api/runs/4/ranking", api.Calls);
        Assert.Contains("AI Engineer Intern", text);
        Assert.Contains("#4 · 06/11/2026 09:00 · Đang phân tích", text);   // giờ Việt Nam
        Assert.Contains("#3 · 05/11/2026 09:00 · Hoàn thành", text);
        Assert.Contains("AI Engineer Intern (2 CV)", text);
        Assert.Contains("Lượt chấm đang chạy: đã xử lý 1/3 CV", text);
        // CV đang chờ chấm: không có điểm, nằm cuối
        AssertOrder(text, "Võ Thành Nam", "Đỗ Chưa Chấm");
        Assert.Contains("Đang chấm Chưa có điểm", text);   // B8: nhãn trạng thái Pending
    }

    [Fact]
    public async Task Changing_run_then_job_updates_url_and_shows_only_that_selection()
    {
        var api = new FakeResultsApi();
        var nav = Nav();
        var (r, sp) = await OpenAsync(api, nav);
        await using var _ = r; using var __ = sp;
        await WaitUntil(() => r.Text.Contains("Võ Thành Nam"), "lượt mới nhất");

        await r.SelectAsync(0, "3");
        Assert.Equal("https://app.test/results?runId=3", nav.Uri);
        await WaitUntil(() => r.Text.Contains("Nguyễn Văn An"), "bảng lượt #3");
        var text = r.Text;
        Assert.Contains("Lượt chấm #3", text);
        Assert.Contains("Backend Developer (.NET) (4 CV)", text);
        Assert.Contains("Business Analyst (2 CV)", text);
        Assert.DoesNotContain("Võ Thành Nam", text);
        Assert.DoesNotContain("AI Engineer Intern (2 CV)", text);   // job của lượt khác không còn trong ô chọn

        await r.SelectAsync(1, "2");
        Assert.Equal("https://app.test/results?runId=3&jobId=2", nav.Uri);
        await WaitUntil(() => r.Text.Contains("Dương Mỹ Linh"), "bảng job Business Analyst");
        Assert.Contains("/api/runs/3/ranking?jobId=2", api.Calls);
        Assert.DoesNotContain("Nguyễn Văn An", r.Text);
        Assert.Contains("Hiển thị 2/2 CV", r.TableText);
    }

    [Fact]
    public async Task Ranking_is_descending_and_unscored_cv_is_last_without_fake_zero()
    {
        var api = new FakeResultsApi();
        var (r, sp) = await OpenAsync(api, Nav("results?runId=3&jobId=1"));
        await using var _ = r; using var __ = sp;
        await WaitUntil(() => r.Text.Contains("Lý Văn Minh"), "bảng xếp hạng");

        var text = r.TableText;
        AssertOrder(text, "Nguyễn Văn An", "Trần Thị Bình", "Lê Hoàng Cường", "Lý Văn Minh");
        var failedRow = text[text.IndexOf("Lý Văn Minh", StringComparison.Ordinal)..];
        // B8: bắt buộc —, kinh nghiệm —, thanh điểm "KW — · AI —", điểm cuối —, nhãn "Lỗi xử lý" (không có điểm 0 giả)
        Assert.StartsWith("Lý Văn Minh CV_14.pdf — — KW — · AI — — Lỗi xử lý Chưa có điểm", failedRow);
        Assert.Contains("01 VA Nguyễn Văn An", text);
        Assert.Contains("Rất phù hợp Dự đoán Đạt", text);
        Assert.Contains("Tiềm năng Dự đoán Không đạt", text);
        Assert.Contains("Tính trên 3 CV có điểm", r.Text);   // trung bình không tính CV lỗi
    }

    [Fact]
    public async Task Search_quick_filter_and_sort_work_on_loaded_ranking()
    {
        var api = new FakeResultsApi();
        var (r, sp) = await OpenAsync(api, Nav("results?runId=3"));
        await using var _ = r; using var __ = sp;
        await WaitUntil(() => r.Text.Contains("Lý Văn Minh"), "bảng xếp hạng");
        var callsBefore = api.Calls.Count;

        await r.SearchAsync("binh");
        var text = r.TableText;
        Assert.Contains("Trần Thị Bình", text);
        Assert.DoesNotContain("Nguyễn Văn An", text);
        Assert.Contains("Hiển thị 1/4 CV", text);

        await r.SearchAsync("KHÔNG CÓ AI");
        Assert.Contains("Không có CV nào khớp bộ lọc", r.Text);
        await r.ClickButtonAsync("Xóa bộ lọc");
        Assert.Contains("Hiển thị 4/4 CV", r.Text);

        await r.ClickButtonAsync("Chưa có điểm", "quick-btn");
        Assert.Contains("Hiển thị 1/4 CV", r.TableText);
        Assert.Contains("Lý Văn Minh", r.TableText);
        Assert.DoesNotContain("Trần Thị Bình", r.TableText);

        await r.ClickButtonAsync("Dự đoán Đạt", "quick-btn");
        Assert.Contains("Hiển thị 1/4 CV", r.TableText);
        Assert.Contains("Nguyễn Văn An", r.TableText);
        Assert.DoesNotContain("Lê Hoàng Cường", r.TableText);

        await r.ClickButtonAsync("Tất cả", "quick-btn");
        await r.SelectAsync(2, nameof(RankingSort.NameAsc));
        AssertOrder(r.TableText, "Lê Hoàng Cường", "Nguyễn Văn An", "Trần Thị Bình", "Lý Văn Minh");

        Assert.Equal(callsBefore, api.Calls.Count);   // tìm/lọc/sắp xếp không gọi lại API
    }

    [Fact]
    public async Task Direct_url_refresh_and_back_forward_load_the_url_selection()
    {
        var api = new FakeResultsApi();
        var nav = Nav("results?runId=3&jobId=2");   // F5 / mở link trực tiếp
        var (r, sp) = await OpenAsync(api, nav);
        await using var _ = r; using var __ = sp;

        await WaitUntil(() => r.Text.Contains("Dương Mỹ Linh"), "mở trực tiếp job 2");
        Assert.DoesNotContain(api.Calls, c => c.Contains("latest"));

        await r.NavigateAsync(nav, "results?runId=3&jobId=1");
        await WaitUntil(() => r.Text.Contains("Nguyễn Văn An"), "đổi job");

        await r.NavigateAsync(nav, "results?runId=3&jobId=2");   // trình duyệt Back
        await WaitUntil(() => r.Text.Contains("Dương Mỹ Linh") && !r.Text.Contains("Nguyễn Văn An"), "Back về job 2");

        await r.NavigateAsync(nav, "results");                     // Back về địa chỉ không tham số → lượt mới nhất
        await WaitUntil(() => r.Text.Contains("Võ Thành Nam"), "Back về lượt mới nhất");
    }

    [Fact]
    public async Task Unknown_run_shows_not_found_state()
    {
        var api = new FakeResultsApi();
        var (r, sp) = await OpenAsync(api, Nav("results?runId=99"));
        await using var _ = r; using var __ = sp;

        await WaitUntil(() => r.Text.Contains("Không tìm thấy lượt chấm #99"), "lượt không tồn tại");
        Assert.Contains("results", r.Hrefs);   // nút "Mở lượt chấm mới nhất"
        Assert.Contains("#99", r.Text);        // ô chọn vẫn giữ lựa chọn trên địa chỉ
    }

    [Fact]
    public async Task Job_not_in_run_lists_valid_jobs_of_that_run()
    {
        var api = new FakeResultsApi();
        var (r, sp) = await OpenAsync(api, Nav("results?runId=3&jobId=99"));
        await using var _ = r; using var __ = sp;

        await WaitUntil(() => r.Text.Contains("Vị trí #99 không thuộc lượt chấm #3"), "job không thuộc lượt");
        Assert.Contains("results?runId=3&jobId=1", r.Hrefs);
        Assert.Contains("results?runId=3&jobId=2", r.Hrefs);
        Assert.DoesNotContain("Nguyễn Văn An", r.Text);   // không lặng lẽ hiện job khác
    }

    [Theory]
    [InlineData("results?runId=abc", "Mã lượt chấm không hợp lệ")]
    [InlineData("results?runId=0", "Mã lượt chấm không hợp lệ")]
    [InlineData("results?runId=3&jobId=-1", "Mã vị trí không hợp lệ")]
    public async Task Invalid_query_values_are_reported_without_calling_ranking(string url, string expected)
    {
        var api = new FakeResultsApi();
        var (r, sp) = await OpenAsync(api, Nav(url));
        await using var _ = r; using var __ = sp;

        await WaitUntil(() => r.Text.Contains(expected), expected);
        Assert.DoesNotContain(api.Calls, c => c.Contains("/ranking") || c.Contains("latest"));
    }

    [Fact]
    public async Task Prompt_test_run_is_not_shown_as_screening_result()
    {
        var api = new FakeResultsApi();
        var backend = BackendRanking();
        api.Rankings[(1, null)] = new RankingResponse
        {
            RunId = 1, RunStatus = RunStatuses.Completed, PromptVersionName = "v1", PassThreshold = 70, JobId = 1,
            JobTitle = backend.JobTitle, Jobs = backend.Jobs, Items = backend.Items
        };
        api.RunTypesById[1] = RunTypes.PromptTest;   // lượt #1 là PromptTest: không có trong danh sách Screening
        var (r, sp) = await OpenAsync(api, Nav("results?runId=1"));
        await using var _ = r; using var __ = sp;

        await WaitUntil(() => r.Text.Contains("Lượt #1 không phải lượt sàng lọc CV"), "lượt PromptTest");
        Assert.DoesNotContain("Nguyễn Văn An", r.Text);
    }

    [Fact]
    public async Task Offline_api_shows_connection_error_then_retry_recovers()
    {
        var api = new FakeResultsApi { Offline = true };
        var (r, sp) = await OpenAsync(api, Nav("results?runId=3"));
        await using var _ = r; using var __ = sp;

        await WaitUntil(() => r.Text.Contains("Không kết nối được máy chủ API"), "lỗi kết nối");
        Assert.Contains("Không tải được danh sách lượt chấm", r.Text);

        api.Offline = false;
        await r.ClickButtonAsync("Thử lại");
        await WaitUntil(() => r.Text.Contains("Nguyễn Văn An"), "thử lại thành công");
        Assert.DoesNotContain("Không tải được danh sách lượt chấm", r.Text);
    }

    [Fact]
    public async Task Server_error_shows_generic_message_without_technical_details()
    {
        var api = new FakeResultsApi { ForceStatus = 500 };
        var (r, sp) = await OpenAsync(api, Nav("results?runId=3"));
        await using var _ = r; using var __ = sp;

        await WaitUntil(() => r.Text.Contains("API trả về lỗi"), "lỗi API");
        Assert.Contains("Máy chủ API gặp lỗi khi tải kết quả", r.Text);
        Assert.Contains("Không tải được danh sách lượt chấm: Máy chủ API gặp lỗi.", r.Text);
        Assert.DoesNotContain("SqlException", r.Text);
    }

    [Fact]
    public async Task Late_response_of_previous_run_does_not_override_new_selection()
    {
        var api = new FakeResultsApi();
        var releaseRun3 = new TaskCompletionSource();
        api.Gate = path => path.StartsWith("/api/runs/3/ranking") ? releaseRun3.Task : Task.CompletedTask;
        var nav = Nav("results?runId=3");
        var (r, sp) = await OpenAsync(api, nav, waitForLoad: false);
        await using var _ = r; using var __ = sp;
        await WaitUntil(() => api.Calls.Contains("/api/runs/3/ranking"), "đang tải lượt #3");
        Assert.Contains("Đang tải bảng xếp hạng lượt chấm #3", r.Text);   // trạng thái Loading

        await r.NavigateAsync(nav, "results?runId=4");   // đổi lượt khi lượt #3 chưa trả về
        await WaitUntil(() => r.Text.Contains("Võ Thành Nam"), "hiện lượt #4");

        releaseRun3.SetResult();             // phản hồi cũ của lượt #3 về muộn
        await Task.Delay(100);
        var text = r.Text;
        Assert.Contains("Võ Thành Nam", text);
        Assert.Contains("Lượt chấm #4", text);
        Assert.DoesNotContain("Nguyễn Văn An", text);
        Assert.Equal("https://app.test/results?runId=4", nav.Uri);
    }

    [Fact]
    public void Results_url_matches_B6_link_format()
    {
        Assert.Equal("results?runId=12", RankingView.ResultsUrl(12));
        Assert.Equal("results?runId=12&jobId=3", RankingView.ResultsUrl(12, 3));
    }
}
