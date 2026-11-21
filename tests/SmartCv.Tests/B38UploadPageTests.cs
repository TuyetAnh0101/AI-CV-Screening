extern alias web;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Components.Upload;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Models;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B38: trang Tải CV chạy bằng renderer Blazor thật (không trình duyệt), API là HttpMessageHandler giả nhưng nhận đúng
/// đường dẫn + multipart thật. Kiểm tra khóa tải lên, metadata A38 theo từng nhóm, vòng hỏi đọc chữ A42 (CTS riêng,
/// bỏ phản hồi cũ), CV trùng, ước tính, hộp "Chữ đã đọc" và chế độ Ẩn danh (B36).
/// </summary>
#pragma warning disable BL0006 // RenderTree APIs chỉ dùng trong renderer kiểm thử.
public class B38UploadPageTests
{
    // ================= API giả =================

    internal sealed record UploadRequest(Dictionary<string, string> Fields, List<string> Files);

    private sealed class Api : HttpMessageHandler
    {
        private readonly List<string> calls = [];
        private readonly List<UploadRequest> uploads = [];
        public IReadOnlyList<string> Calls { get { lock (calls) return [.. calls]; } }
        public IReadOnlyList<UploadRequest> Uploads { get { lock (uploads) return [.. uploads]; } }
        public ConcurrentDictionary<string, Guid> IdsByName { get; } = new();
        public Func<UploadRequest, Task<HttpResponseMessage>> Upload { get; set; }
        public Func<Guid, Task<HttpResponseMessage>> Detail { get; set; }
        public Func<Guid, Task<HttpResponseMessage>> Text { get; set; }
        public Func<int, Task<HttpResponseMessage>> Estimate { get; set; }
        public Func<string, Task<HttpResponseMessage>> Applications { get; set; } = _ => throw new InvalidOperationException("Không mong gọi /api/applications");
        public Func<int, Task<HttpResponseMessage>> GetRun { get; set; } = _ => throw new InvalidOperationException("Không mong gọi GET /api/runs/{id}");
        public string? LastApplicationsBody { get; private set; }

        public Api()
        {
            Upload = r => Task.FromResult(Json(AcceptAll(r)));
            Detail = id => Task.FromResult(Json(new CvDetailDto { CvId = id, ExtractionStatus = ExtractionStatuses.Pending }));
            Text = id => Task.FromResult(Json(new CvTextDto { CvId = id, ExtractionStatus = ExtractionStatuses.Pending, StatusMessage = "CV đang chờ đọc chữ." }));
            Estimate = n => Task.FromResult(Json(new RunEstimateDto { Count = n, Text = $"{n} CV · dưới 1 phút", BasedOnRuns = 2 }));
        }

        public Guid IdOf(string name) => IdsByName.GetOrAdd(name, _ => Guid.NewGuid());
        public string? NameOf(Guid id) => IdsByName.FirstOrDefault(p => p.Value == id).Key;
        public int CallCount(string contains) => Calls.Count(c => c.Contains(contains, StringComparison.Ordinal));

        public CvUploadResponse AcceptAll(UploadRequest r) => new()
        {
            TotalFiles = r.Files.Count, SuccessCount = r.Files.Count,
            Files = [.. r.Files.Select(name => new CvUploadItemResult { FileName = name, Status = CvUploadStatuses.Accepted, CvId = IdOf(name) })]
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (calls) calls.Add($"{request.Method} {request.RequestUri.PathAndQuery}");
            if (path == "/api/jobs")
                return Json(new[] { new JobDto { JobId = 1, Title = "Backend Developer (.NET)", IsActive = true },
                                    new JobDto { JobId = 2, Title = "Tester (QA)", IsActive = true } });
            if (request.Method == HttpMethod.Post && path == "/api/cvs/upload")
            {
                var fields = new Dictionary<string, string>();
                var files = new List<string>();
                foreach (var part in (MultipartFormDataContent)request.Content!)
                {
                    var disposition = part.Headers.ContentDisposition!;
                    if (disposition.FileName is { } file) { files.Add(file.Trim('"')); await part.CopyToAsync(Stream.Null, ct); }   // đọc như HttpClient: lần lượt
                    else fields[disposition.Name!.Trim('"')] = await part.ReadAsStringAsync(ct);
                }
                var upload = new UploadRequest(fields, files);
                lock (uploads) uploads.Add(upload);
                return await Upload(upload);
            }
            var cv = Regex.Match(path, "^/api/cvs/([0-9a-f-]{36})(/text)?$");
            if (request.Method == HttpMethod.Get && cv.Success)
            {
                var id = Guid.Parse(cv.Groups[1].Value);
                return await (cv.Groups[2].Success ? Text(id) : Detail(id));
            }
            if (path == "/api/runs/estimate")
                return await Estimate(int.Parse(Regex.Match(request.RequestUri.Query, @"count=(\d+)").Groups[1].Value));
            if (request.Method == HttpMethod.Post && path == "/api/applications")
            {
                LastApplicationsBody = await request.Content!.ReadAsStringAsync(ct);
                return await Applications(LastApplicationsBody);
            }
            if (request.Method == HttpMethod.Post && path == "/api/runs")
                return Json(Run(31, RunStatuses.Queued), HttpStatusCode.Created);
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/runs/", StringComparison.Ordinal))
                return await GetRun(int.Parse(path["/api/runs/".Length..]));
            throw new InvalidOperationException($"Request không mong đợi: {request.Method} {path}");
        }
    }

    private static HttpResponseMessage Json<T>(T body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(body) };

    private static HttpResponseMessage Problem(int status, string title, string? detail = null) =>
        new((HttpStatusCode)status) { Content = JsonContent.Create(new { title, detail, status }) };

    private static RunProgressDto Run(int id, string status) =>
        new(id, RunTypes.Screening, status, 2, "v2", "gemini-2.5-flash", 0.2m, 0.3m, 0.7m, 70m, 1, status == RunStatuses.Completed ? 1 : 0, 0,
            status == RunStatuses.Completed ? 100 : 0, new DateTime(2026, 11, 17, 2, 0, 0, DateTimeKind.Utc), null);

    private static CvDetailDto Completed(Guid id, string level, int chars) => new()
    {
        CvId = id, ExtractionStatus = ExtractionStatuses.Completed, ExtractionMethod = ExtractionMethods.ImageOcr,
        ExtractedText = "nội dung không dùng ở bảng",
        TextQuality = new TextQualityDto
        {
            CharCount = chars, WordCount = chars / 5, LetterRatio = level == TextQualityLevels.Good ? .85m : .68m, Level = level,
            MaybeMisread = level != TextQualityLevels.Good,
            Warnings = level == TextQualityLevels.Good ? [] : [$"Chỉ đọc được {chars} ký tự (CV thường trên 500): có thể đọc thiếu.", TextQuality.OcrAdvice]
        }
    };

    private static CvTextDto TextDto(Guid id, string level, string? text) => new()
    {
        CvId = id, OriginalFileName = "ten-luu-tren-may-chu.png", ExtractionStatus = ExtractionStatuses.Completed,
        ExtractionMethod = ExtractionMethods.ImageOcr, Text = text, StatusMessage = "Đã đọc xong.",
        Quality = Completed(id, level, text?.Length ?? 0).TextQuality
    };

    // ================= Renderer kiểm thử =================

    private sealed class R(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", isInteractive: true);
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        private int rootId;

        public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
        {
            rootId = AssignRootComponentId(InstantiateComponent(typeof(Upload)));
            await RenderRootComponentAsync(rootId);
        });

        private List<RenderTreeFrame> All(int? id = null)
        {
            var frames = GetCurrentRenderTreeFrames(id ?? rootId);
            return frames.Array.Take(frames.Count).SelectMany(f => f.FrameType == RenderTreeFrameType.Component
                ? new[] { f }.Concat(All(f.ComponentId)) : [f]).ToList();
        }

        private T On<T>(Func<T> read) => Dispatcher.InvokeAsync(read).Result;

        public string Text => On(() => Regex.Replace(WebUtility.HtmlDecode(string.Concat(All().Select(f => f.FrameType switch
        {
            RenderTreeFrameType.Text => f.TextContent,
            RenderTreeFrameType.Markup => Regex.Replace(f.MarkupContent, "<[^>]+>", " "),
            _ => " "
        }))), @"\s+", " "));

        public List<string?> AttributeValues(string name) => On(() => All()
            .Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == name).Select(f => f.AttributeValue as string).ToList());

        public List<UploadItem> Items => On(() => (List<UploadItem>)All()
            .First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "Items").AttributeValue);

        public ExtractedTextDialog? Dialog => On(() => All().Where(f => f.FrameType == RenderTreeFrameType.Component)
            .Select(f => f.Component).OfType<ExtractedTextDialog>().FirstOrDefault());

        private static Dictionary<string, object?> ElementAttributes(List<RenderTreeFrame> frames, int attributeIndex)
        {
            var element = frames.Take(attributeIndex).ToList().FindLastIndex(f => f.FrameType == RenderTreeFrameType.Element);
            return frames.Skip(element + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute)
                .GroupBy(f => f.AttributeName).ToDictionary(g => g.Key, g => (object?)g.Last().AttributeValue);
        }

        /// <summary>Thuộc tính của phần tử có id cho trước (null nếu không có trên trang).</summary>
        public Dictionary<string, object?>? Element(string id) => On(() =>
        {
            var frames = All();
            var index = frames.FindIndex(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "id" && Equals(f.AttributeValue, id));
            return index < 0 ? null : ElementAttributes(frames, index);
        });

        /// <summary>Ô chọn file của InputFile (phần tử input type=file).</summary>
        public Dictionary<string, object?> FileInput => On(() =>
        {
            var frames = All();
            return ElementAttributes(frames, frames.FindIndex(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "type" && Equals(f.AttributeValue, "file")));
        });

        private ulong Handler(string id, string eventName)
        {
            var attributes = Element(id) ?? throw new InvalidOperationException($"Không thấy #{id}");
            return On(() =>
            {
                var frames = All();
                var index = frames.FindIndex(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "id" && Equals(f.AttributeValue, id));
                var element = frames.Take(index).ToList().FindLastIndex(f => f.FrameType == RenderTreeFrameType.Element);
                return frames.Skip(element + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute)
                    .First(f => f.AttributeName == eventName).AttributeEventHandlerId;
            });
        }

        public Task ChangeAsync(string id, object? value) => Dispatcher.InvokeAsync(() => DispatchEventAsync(Handler(id, "onchange"), null, new ChangeEventArgs { Value = value }));

        public Task ClickIdAsync(string id) => Dispatcher.InvokeAsync(() => DispatchEventAsync(Handler(id, "onclick"), null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));

        /// <summary>Bấm nút đầu tiên khớp điều kiện (chữ của cả nút, thuộc tính); trả Task của handler.</summary>
        private Task<Task> ClickWhereAsync(Func<string, Dictionary<string, object?>, bool> match, string description) => Dispatcher.InvokeAsync<Task>(() =>
        {
            var handler = FindButton(rootId, match);
            if (handler == 0) throw new InvalidOperationException($"Không thấy nút {description}");
            return DispatchEventAsync(handler, null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        });

        private ulong FindButton(int componentId, Func<string, Dictionary<string, object?>, bool> match)
        {
            var current = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < current.Count; i++)
            {
                var frame = current.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component)
                {
                    var child = FindButton(frame.ComponentId, match);
                    if (child != 0) return child;
                }
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                var subtree = current.Array.Skip(i).Take(frame.ElementSubtreeLength).ToList();
                var attributes = subtree.Skip(1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToList();
                var values = attributes.GroupBy(f => f.AttributeName).ToDictionary(g => g.Key, g => (object?)g.Last().AttributeValue);
                var text = Regex.Replace(string.Concat(subtree.Select(f => f.FrameType switch
                {
                    RenderTreeFrameType.Text => f.TextContent,
                    RenderTreeFrameType.Markup => Regex.Replace(f.MarkupContent, "<[^>]+>", " "),
                    _ => " "
                })), @"\s+", " ").Trim();
                var click = attributes.FirstOrDefault(f => f.AttributeName == "onclick");
                if (click.AttributeEventHandlerId != 0 && match(text, values)) return click.AttributeEventHandlerId;
            }
            return 0;
        }

        public Task<Task> ClickAsync(string text) => ClickWhereAsync((t, _) => t.Contains(text, StringComparison.Ordinal), $"\"{text}\"");

        public Task<Task> ClickClassAsync(string cssClass) =>
            ClickWhereAsync((_, a) => a.TryGetValue("class", out var c) && (c as string)?.Split(' ').Contains(cssClass) == true, "." + cssClass);

        /// <summary>Thuộc tính của nút có class cho trước.</summary>
        public Dictionary<string, object?>? ButtonByClass(string cssClass) => On(() =>
        {
            var frames = All();
            var index = frames.FindIndex(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "class"
                                              && (f.AttributeValue as string)?.Split(' ').Contains(cssClass) == true);
            return index < 0 ? null : ElementAttributes(frames, index);
        });

        private T Parameter<T>(string name) => On(() => (T)All().First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == name).AttributeValue);

        /// <summary>Thả file vào ô kéo thả (gọi OnFilesSelected như InputFile). Task xong khi cả lần tải xong.</summary>
        public Task DropAsync(params IBrowserFile[] files)
        {
            var callback = Parameter<EventCallback<InputFileChangeEventArgs>>("OnFilesSelected");
            return Dispatcher.InvokeAsync(() => callback.InvokeAsync(new InputFileChangeEventArgs(files)));
        }

        public Task ViewTextAsync(UploadItem item, string returnFocusId = "cv-list-title")
        {
            var callback = Parameter<EventCallback<(UploadItem, string)>>("OnViewText");
            return Dispatcher.InvokeAsync(() => callback.InvokeAsync((item, returnFocusId)));
        }

        public Task RemoveAsync(UploadItem item)
        {
            var callback = Parameter<EventCallback<UploadItem>>("OnRemove");
            return Dispatcher.InvokeAsync(() => callback.InvokeAsync(item));
        }

        public Task ToggleAsync(UploadItem item, bool selected)
        {
            var callback = Parameter<EventCallback<(UploadItem, bool)>>("OnToggle");
            return Dispatcher.InvokeAsync(() => callback.InvokeAsync((item, selected)));
        }

        public Task EnableUploadAsync(string source = "Email") => Dispatcher.InvokeAsync(async () =>
        {
            await DispatchEventAsync(Handler("cv-source", "onchange"), null, new ChangeEventArgs { Value = source });
            await DispatchEventAsync(Handler("cv-consent", "onchange"), null, new ChangeEventArgs { Value = true });
        });
    }

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private sealed class Tracker { public int Open, MaxOpen, Total; }

    private sealed class FakeFile(string name, long size = 2048, Tracker? tracker = null, string contentType = "application/pdf") : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => size;
        public string ContentType => contentType;
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (tracker is not null) { lock (tracker) { tracker.Total++; tracker.Open++; tracker.MaxOpen = Math.Max(tracker.MaxOpen, tracker.Open); } }
            return new TrackedStream(new byte[Math.Min(size, 64)], tracker);
        }
        private sealed class TrackedStream(byte[] bytes, Tracker? tracker) : MemoryStream(bytes)
        {
            private bool closed;
            protected override void Dispose(bool disposing)
            {
                if (!closed && tracker is not null) { closed = true; lock (tracker) tracker.Open--; }
                base.Dispose(disposing);
            }
        }
    }

    private static ServiceProvider Services(Api api, AnonymityState? anonymity = null, TestNavigationManager? nav = null,
        Dictionary<string, string?>? settings = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["ApiBaseUrl"] = "https://api.test/",
            ["CvUpload:MaxFilesPerSelection"] = "25",   // như appsettings.json của Web
            ["RunProgress:PollIntervalMs"] = "30",
            ["TextCheck:PollIntervalMs"] = "25",
            ["RunEstimate:DebounceMs"] = "5"
        };
        foreach (var (key, value) in settings ?? []) config[key] = value;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.Configure<CvUploadOptions>(o => { o.MaxFileSizeMB = 2; o.MaxFilesPerRequest = 20; });   // như appsettings.json
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>();
        services.AddSingleton<NavigationManager>(nav ?? new TestNavigationManager());
        services.AddSupplyValueFromQueryProvider();
        services.AddSingleton(anonymity ?? B36PrivacyTests.NormalView());
        var http = new HttpClient(api) { BaseAddress = new Uri("https://api.test/") };
        services.AddSingleton(new JobApiClient(http, NullLogger<JobApiClient>.Instance));
        services.AddSingleton(new CvApiClient(http, Microsoft.Extensions.Options.Options.Create(new CvUploadOptions { MaxFileSizeMB = 2 }), NullLogger<CvApiClient>.Instance));
        services.AddSingleton(new ApplicationApiClient(http, NullLogger<ApplicationApiClient>.Instance));
        services.AddSingleton(new RunApiClient(http, NullLogger<RunApiClient>.Instance));
        return services.AddFixtureFileDownloads(http).BuildServiceProvider();
    }

    private static async Task<R> OpenAsync(ServiceProvider services)
    {
        var r = new R(services);
        await r.StartAsync();
        return r;
    }

    private static async Task WaitUntil(Func<bool> condition, string because, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Hết thời gian chờ: {because}");
            await Task.Delay(10);
        }
    }

    private static bool IsTrue(Dictionary<string, object?>? attributes, string name) =>
        attributes is not null && attributes.TryGetValue(name, out var value) && value is true;

    private static IBrowserFile[] Files(int count, Tracker? tracker = null, string prefix = "cv") =>
        [.. Enumerable.Range(1, count).Select(i => new FakeFile($"{prefix}-{i:D2}.pdf", 4096, tracker))];

    // ================= (1) Nguồn + đồng ý khóa tải lên =================

    [Fact]
    public async Task Locked_until_source_and_consent_input_disabled_and_no_upload_request()
    {
        var api = new Api();
        await using var services = Services(api);
        await using var r = await OpenAsync(services);

        Assert.True(IsTrue(r.FileInput, "disabled"));
        Assert.False(IsTrue(r.Element("cv-consent"), "checked"));            // không tích sẵn
        Assert.True(string.IsNullOrEmpty(r.Element("cv-source")!.GetValueOrDefault("value") as string));   // chưa chọn nguồn
        var text = r.Text;
        Assert.Contains("Chưa mở tải lên", text);
        Assert.Contains("Chọn nguồn CV và tích xác nhận ứng viên đã đồng ý trước khi tải lên.", text);
        Assert.Contains("Còn thiếu 2 mục", text);
        Assert.Contains(UploadRules.ConsentText, text);

        // Sự kiện chọn file tới trong lúc khóa (ví dụ ngay trước khi khóa): không tạo dòng, không gửi
        await r.DropAsync(new FakeFile("a.pdf"));
        Assert.Empty(r.Items);
        Assert.DoesNotContain(api.Calls, c => c.Contains("/api/cvs"));

        await r.ChangeAsync("cv-source", "Email");
        Assert.Contains("Tích xác nhận ứng viên đã đồng ý trước khi tải lên.", r.Text);
        Assert.True(IsTrue(r.FileInput, "disabled"));
        await r.ChangeAsync("cv-consent", true);
        Assert.False(IsTrue(r.FileInput, "disabled"));
        Assert.Contains("Tải CV lên để phân tích", r.Text);
        Assert.Contains("Đã đủ điều kiện", r.Text);
        Assert.DoesNotContain("Chưa mở tải lên", r.Text);   // lỗi "chưa mở" cũ tự biến mất khi đã đủ
    }

    [Theory]
    [InlineData(2, new[] { 2 })]
    [InlineData(5, new[] { 5 })]
    [InlineData(25, new[] { 20, 5 })]
    public async Task Each_batch_carries_the_same_source_and_consent_and_streams_one_file_at_a_time(int count, int[] batches)
    {
        var api = new Api();
        var tracker = new Tracker();
        await using var services = Services(api);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync("JobBoard");

        await r.DropAsync(Files(count, tracker));

        Assert.Equal(batches, api.Uploads.Select(u => u.Files.Count));
        Assert.All(api.Uploads, u => Assert.Equal(new Dictionary<string, string> { ["sourceChannel"] = "JobBoard", ["consentConfirmed"] = "true" }, u.Fields));
        Assert.Equal(1, tracker.MaxOpen);   // BrowserFileContent: không mở trước stream của cả nhóm
        Assert.Equal(count, tracker.Total);
        Assert.Equal(0, tracker.Open);
        Assert.Equal(count, r.Items.Count(i => i.State == UploadItemState.Uploaded && i.CanAnalyze));
        Assert.Contains($"Kết quả tải lên: {count} CV mới.", r.Text);
        await WaitUntil(() => r.Text.Contains($"{count} CV · dưới 1 phút"), "ước tính cho số CV vừa tải");
        Assert.Equal(1, api.CallCount($"/api/runs/estimate?count={count}"));
    }

    [Fact]
    public async Task Metadata_is_a_snapshot_controls_lock_while_uploading_and_the_page_stays_responsive()
    {
        var api = new Api();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Upload = async u => { if (api.Uploads.Count == 1) await release.Task; return Json(api.AcceptAll(u)); };
        await using var services = Services(api);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync("Email");

        var upload = r.DropAsync(Files(25));
        await WaitUntil(() => api.Uploads.Count == 1, "nhóm 1 đang gửi");

        Assert.True(IsTrue(r.Element("cv-source"), "disabled"));
        Assert.True(IsTrue(r.Element("cv-consent"), "disabled"));
        Assert.True(IsTrue(r.FileInput, "disabled"));
        Assert.Contains("Đang gửi nhóm 1/2 · 20 CV", r.Text);

        // Trang vẫn xử lý thao tác khác khi nhóm 1 chưa xong (circuit không bị chặn)
        await r.ChangeAsync("job", "2");
        Assert.Contains("Không yêu cầu GPA", r.Text);
        // Dù sự kiện đổi nguồn lọt qua (ô đã khóa trên trình duyệt), các nhóm sau vẫn gửi đúng nguồn đã chốt
        await r.ChangeAsync("cv-source", "Website");

        release.SetResult();
        await upload;
        Assert.Equal(["Email", "Email"], api.Uploads.Select(u => u.Fields["sourceChannel"]));
        Assert.False(IsTrue(r.Element("cv-source"), "disabled"));
        Assert.False(IsTrue(r.Element("cv-consent"), "disabled"));
        Assert.Equal(25, r.Items.Count(i => i.CanAnalyze));
    }

    [Fact]
    public async Task Http_400_consent_rejection_explains_the_fix_marks_nothing_saved_and_stops_other_batches()
    {
        var api = new Api
        {
            Upload = _ => Task.FromResult(Problem(400, "Chưa xác nhận đồng ý",
                "Hãy xác nhận ứng viên đã đồng ý xử lý dữ liệu tuyển dụng (consentConfirmed = true) trước khi tải CV."))
        };
        await using var services = Services(api);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();

        await r.DropAsync(Files(25));

        Assert.Single(api.Uploads);                                         // không tự gửi nhóm 2, không tự thử lại
        var text = r.Text;
        Assert.Contains("Máy chủ chưa nhận lần tải này", text);
        Assert.Contains("Kiểm tra lại nguồn CV và ô xác nhận đồng ý", text);
        Assert.DoesNotContain("consentConfirmed", text);
        Assert.All(r.Items, i => Assert.False(i.CanAnalyze));
        Assert.Equal(20, r.Items.Count(i => i.Message!.StartsWith("Chưa lưu:")));
        Assert.Equal(5, r.Items.Count(i => i.Message == UploadRules.NotSentMessage));
        Assert.Equal("Email", r.Element("cv-source")!["value"]);             // giữ nguyên lựa chọn để HR sửa
        Assert.True(IsTrue(r.Element("cv-consent"), "checked"));
        await Task.Delay(150);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("GET /api/cvs/") || c.Contains("estimate"));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("offline")]
    public async Task Timeout_or_lost_connection_never_marks_files_saved(string failure)
    {
        var api = new Api
        {
            Upload = _ => failure == "timeout" ? throw new TaskCanceledException("HttpClient.Timeout") : throw new HttpRequestException("Connection refused")
        };
        await using var services = Services(api);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();

        await r.DropAsync(Files(2));

        Assert.Contains("Không kết nối được máy chủ API", r.Text);
        Assert.DoesNotContain("Connection refused", r.Text);
        Assert.All(r.Items, i => Assert.Equal((UploadItemState.Rejected, (Guid?)null), (i.State, i.CvId)));
        Assert.Contains("Kết quả tải lên: 0 CV mới, 2 file bị từ chối.", r.Text);
    }

    [Fact]
    public async Task Cancel_during_upload_does_not_send_the_remaining_batches()
    {
        var api = new Api();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Upload = async u => { await release.Task; return Json(api.AcceptAll(u)); };
        await using var services = Services(api);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();

        var upload = r.DropAsync(Files(25));
        await WaitUntil(() => api.Uploads.Count == 1, "nhóm 1 đang gửi");
        await await r.ClickAsync("Hủy");
        Assert.Empty(r.Items);
        release.SetResult();
        await upload;

        Assert.Single(api.Uploads);   // nhóm 2 (5 CV) không được gửi sau khi Hủy
        Assert.Empty(r.Items);
        Assert.DoesNotContain("Kết quả tải lên", r.Text);
        await Task.Delay(150);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("GET /api/cvs/"));   // không hỏi đọc chữ cho dòng đã bỏ
    }

    // ================= (2) Theo dõi đọc chữ (A42) =================

    [Fact]
    public async Task Text_polling_shows_amber_warning_for_low_quality_and_stops_when_done()
    {
        var api = new Api();
        var rounds = new ConcurrentDictionary<Guid, int>();
        api.Detail = id =>
        {
            var n = rounds.AddOrUpdate(id, 1, (_, v) => v + 1);
            var name = api.NameOf(id)!;
            return Task.FromResult(Json(n < 2 ? new CvDetailDto { CvId = id, ExtractionStatus = ExtractionStatuses.Pending }
                : name.Contains("mo") ? Completed(id, TextQualityLevels.Low, 182) : Completed(id, TextQualityLevels.Good, 724)));
        };
        await using var services = Services(api);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();
        await r.DropAsync(new FakeFile("cv-anh-ro.png", 4096, contentType: "image/png"), new FakeFile("cv-anh-mo.png", 4096, contentType: "image/png"));

        Assert.Contains("Đang đọc chữ…", r.Text);
        await WaitUntil(() => r.Text.Contains("⚠ Có thể đọc sai · Xem chữ đã đọc"), "cảnh báo ảnh mờ");
        await WaitUntil(() => r.Text.Contains("Đọc tốt"), "ảnh rõ đọc tốt");
        var text = r.Text;
        Assert.Contains("724 ký tự", text);
        Assert.Contains("Cần xem lại 1", text);
        Assert.Contains("1 CV có thể bị đọc sai hoặc không đọc được chữ.", text);
        Assert.Contains("Bỏ chọn 1 CV cần xem lại", text);
        Assert.Contains("needs-review", r.AttributeValues("class"));
        Assert.DoesNotContain("nội dung không dùng ở bảng", text);   // chữ CV không bao giờ vào bảng

        var calls = api.CallCount("GET /api/cvs/");
        await Task.Delay(200);
        Assert.Equal(calls, api.CallCount("GET /api/cvs/"));         // đọc xong: dừng hỏi
        Assert.Equal(4, calls);                                       // 2 CV × (Pending, Completed)

        // HR có thể (không bắt buộc) bỏ chọn CV cần xem lại; ước tính cập nhật theo
        await await r.ClickAsync("Bỏ chọn 1 CV cần xem lại");
        await WaitUntil(() => r.Text.Contains("1 CV · dưới 1 phút"), "ước tính sau khi bỏ chọn");
        Assert.True(r.Items.Single(i => i.FileName == "cv-anh-mo.png").CanAnalyze);   // vẫn chọn lại được
    }

    [Fact]
    public async Task Text_polling_gives_up_after_the_time_limit_and_failed_extraction_is_reported()
    {
        var api = new Api();
        api.Detail = id => Task.FromResult(Json(api.NameOf(id) == "loi.pdf"
            ? new CvDetailDto { CvId = id, ExtractionStatus = ExtractionStatuses.Failed, ExtractionError = "Tesseract: lỗi kỹ thuật" }
            : new CvDetailDto { CvId = id, ExtractionStatus = ExtractionStatuses.Pending }));
        await using var services = Services(api, settings: new() { ["TextCheck:TimeoutMs"] = "200" });
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();
        await r.DropAsync(new FakeFile("cho-mai.pdf"), new FakeFile("loi.pdf"));

        await WaitUntil(() => r.Text.Contains("Chưa đọc xong sau 2 phút"), "quá thời gian chờ");
        Assert.Contains("⚠ Không đọc được chữ · Xem chi tiết", r.Text);
        Assert.DoesNotContain("Tesseract", r.Text);                   // không lộ lỗi kỹ thuật
        Assert.Contains("Kiểm tra lại", r.Text);
        var calls = api.CallCount("GET /api/cvs/");
        await Task.Delay(200);
        Assert.Equal(calls, api.CallCount("GET /api/cvs/"));
    }

    [Fact]
    public async Task Late_text_response_for_a_removed_row_is_ignored_and_dispose_stops_polling()
    {
        var api = new Api();
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        api.Detail = id =>
        {
            if (api.NameOf(id) == "xoa.pdf" && first) { first = false; return held.Task; }
            return Task.FromResult(Json(new CvDetailDto { CvId = id, ExtractionStatus = ExtractionStatuses.Pending }));
        };
        await using var services = Services(api);
        var r = await OpenAsync(services);
        await r.EnableUploadAsync();
        await r.DropAsync(new FakeFile("xoa.pdf"), new FakeFile("giu.pdf"));
        await WaitUntil(() => api.CallCount($"GET /api/cvs/{api.IdOf("xoa.pdf")}") == 1, "đang hỏi CV sẽ bị xóa");

        var removed = r.Items.Single(i => i.FileName == "xoa.pdf");
        await r.RemoveAsync(removed);
        held.SetResult(Json(Completed(api.IdOf("xoa.pdf"), TextQualityLevels.Low, 100)));
        await WaitUntil(() => api.CallCount($"GET /api/cvs/{api.IdOf("giu.pdf")}") >= 2, "vòng hỏi tiếp tục cho dòng còn lại");

        Assert.DoesNotContain(r.Items, i => i.FileName == "xoa.pdf");
        Assert.Null(removed.Quality);                                  // phản hồi cũ không ghi vào dòng đã bỏ
        Assert.DoesNotContain("Cần xem lại", r.Text);
        Assert.Equal(1, api.CallCount($"GET /api/cvs/{api.IdOf("xoa.pdf")}"));

        await r.DisposeAsync();                                        // rời trang
        var calls = api.CallCount("GET /api/cvs/");
        await Task.Delay(150);
        Assert.Equal(calls, api.CallCount("GET /api/cvs/"));
    }

    [Fact]
    public async Task Cancel_while_a_text_request_hangs_then_new_upload_starts_a_fresh_polling_loop()
    {
        var api = new Api();
        var hung = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Detail = id => api.NameOf(id) == "cu.pdf" ? hung.Task   // máy chủ treo, không để ý CancellationToken
            : Task.FromResult(Json(Completed(id, TextQualityLevels.Good, 900)));
        await using var services = Services(api);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();
        await r.DropAsync(new FakeFile("cu.pdf"));
        await WaitUntil(() => api.CallCount($"GET /api/cvs/{api.IdOf("cu.pdf")}") == 1, "vòng cũ đang chờ phản hồi");

        await await r.ClickAsync("Hủy");
        await r.DropAsync(new FakeFile("moi.pdf"));
        await WaitUntil(() => r.Text.Contains("Đọc tốt"), "vòng mới hỏi CV mới dù vòng cũ còn treo");

        hung.SetResult(Json(Completed(api.IdOf("cu.pdf"), TextQualityLevels.Low, 100)));   // phản hồi cũ về muộn
        await Task.Delay(150);
        Assert.Equal(["moi.pdf"], r.Items.Select(i => i.FileName));
        Assert.DoesNotContain("Cần xem lại", r.Text);
        Assert.Equal(1, api.CallCount($"GET /api/cvs/{api.IdOf("moi.pdf")}"));               // không có hai vòng hỏi song song
    }

    [Fact]
    public async Task Text_polling_has_its_own_cancellation_independent_of_run_progress_polling()
    {
        var api = new Api { GetRun = id => Task.FromResult(Json(Run(id, RunStatuses.Completed))) };
        var nav = new TestNavigationManager();
        await using var services = Services(api, nav: nav);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();
        await r.DropAsync(new FakeFile("dang-doc.pdf"));
        await WaitUntil(() => api.CallCount("GET /api/cvs/") >= 1, "bắt đầu hỏi đọc chữ");

        await r.Dispatcher.InvokeAsync(() => nav.NavigateTo("upload?runId=7"));
        await WaitUntil(() => r.Text.Contains("Lượt phân tích #7"), "mở lượt 7");
        await r.Dispatcher.InvokeAsync(() => nav.NavigateTo("upload"));      // StopTracking của tiến độ phân tích
        var before = api.CallCount("GET /api/cvs/");
        await WaitUntil(() => api.CallCount("GET /api/cvs/") > before + 1, "vòng hỏi đọc chữ vẫn chạy sau khi dừng theo dõi lượt");
        Assert.Contains("dang-doc.pdf", r.Text);                             // danh sách giữ nguyên
    }

    // ================= (3) CV trùng (A42 existing*) =================

    [Fact]
    public async Task Duplicate_with_existing_id_is_selectable_and_analysed_for_a_new_job_without_reupload()
    {
        var api = new Api();
        var oldId = Guid.NewGuid();
        api.Upload = _ => Task.FromResult(Json(new CvUploadResponse
        {
            TotalFiles = 1, FailedCount = 1,
            Files = [new() { FileName = "cv-anh-ro.png", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate,
                             ErrorMessage = "Trùng với CV đã tải trước đó: \"ten-file-cu.png\" (ngày 05/11/2026), đã ứng tuyển: Backend Developer (.NET).",
                             ExistingCvId = oldId, ExistingFileName = "ten-file-cu.png",
                             ExistingUploadedAt = new DateTime(2026, 11, 5, 2, 0, 0, DateTimeKind.Utc), ExistingJobTitles = ["Backend Developer (.NET)"] }]
        }));
        api.Detail = id => Task.FromResult(Json(Completed(id, TextQualityLevels.Good, 724)));
        api.Applications = _ => Task.FromResult(Json(new CreateApplicationsResponse
        { JobId = 2, Items = [new() { CvId = oldId, ApplicationId = 99, Status = ApplicationItemStatuses.Created }] }));
        api.GetRun = id => Task.FromResult(Json(Run(id, RunStatuses.Completed)));
        var nav = new TestNavigationManager();
        await using var services = Services(api, nav: nav);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();

        await r.DropAsync(new FakeFile("cv-anh-ro.png", 4096, contentType: "image/png"));

        var item = Assert.Single(r.Items);
        Assert.Equal((UploadItemState.Existing, (Guid?)oldId, true, true), (item.State, item.CvId, item.CanAnalyze, item.Selected));
        var text = r.Text;
        Assert.Contains("Đã có sẵn", text);
        Assert.Contains("Đã có từ ngày 05/11/2026; đã ứng tuyển: Backend Developer (.NET). Vẫn chọn được để phân tích.", text);
        Assert.Contains("Kết quả tải lên: 0 CV mới, 1 CV đã có sẵn.", text);
        Assert.DoesNotContain("ten-file-cu", text);                                   // không hiện tên file của hồ sơ cũ
        Assert.DoesNotContain(r.AttributeValues("title"), t => t?.Contains("ten-file-cu") == true);
        await WaitUntil(() => r.Text.Contains("Đọc tốt"), "chất lượng chữ của CV cũ");

        await r.ChangeAsync("job", "2");
        await await r.ClickClassAsync("sc-btn-analyze");
        Assert.Contains(oldId.ToString(), api.LastApplicationsBody);
        Assert.Contains("\"jobId\":2", api.LastApplicationsBody);
        Assert.Single(api.Uploads);                                                   // không upload lại
        Assert.Equal("https://app.test/upload?runId=31", nav.Uri);
    }

    // ================= (4) Ước tính thời gian =================

    [Fact]
    public async Task Estimate_follows_the_selection_ignores_stale_answers_and_degrades_without_blocking_analysis()
    {
        var api = new Api();
        var slowTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = false;
        api.Estimate = async n =>
        {
            if (fail) return Problem(500, "boom");
            if (n == 2) await slowTwo.Task;
            return Json(new RunEstimateDto { Count = n, Text = $"{n} CV · khoảng {n} phút", BasedOnRuns = 4, AiSimulated = true });
        };
        await using var services = Services(api);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();
        await r.DropAsync(new FakeFile("a.pdf"), new FakeFile("b.pdf"));
        await WaitUntil(() => api.CallCount("estimate?count=2") == 1, "hỏi ước tính cho 2 CV");
        Assert.Contains("Đang ước tính thời gian…", r.Text);

        await r.ToggleAsync(r.Items[1], false);
        await WaitUntil(() => r.Text.Contains("1 CV · khoảng 1 phút"), "ước tính cho 1 CV");
        Assert.Contains("Ước tính theo 4 lượt chấm gần nhất · AI giả lập", r.Text);
        slowTwo.SetResult();                                         // câu trả lời cũ (2 CV) về muộn
        await Task.Delay(100);
        Assert.Contains("1 CV · khoảng 1 phút", r.Text);
        Assert.DoesNotContain("2 CV · khoảng 2 phút", r.Text);

        fail = true;
        await r.ToggleAsync(r.Items[1], true);
        await WaitUntil(() => r.Text.Contains("Chưa ước tính được thời gian, vẫn phân tích được."), "API ước tính lỗi");
        Assert.Contains("2 CV", r.Text);
        Assert.False(IsTrue(r.Element("job"), "disabled"));
        Assert.False(IsTrue(r.ButtonByClass("sc-btn-analyze"), "disabled"));   // lỗi ước tính không khóa nút phân tích

        await r.ToggleAsync(r.Items[0], false);
        await r.ToggleAsync(r.Items[1], false);
        await Task.Delay(100);
        Assert.Contains("Chưa chọn CV nào", r.Text);
        Assert.Equal(0, api.CallCount("estimate?count=0"));          // 0 CV: không gọi API
    }

    [Fact]
    public async Task Estimate_for_a_count_already_answered_is_reused_without_another_request()
    {
        var api = new Api();
        await using var services = Services(api);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();
        await r.DropAsync(new FakeFile("a.pdf"), new FakeFile("b.pdf"));
        await WaitUntil(() => r.Text.Contains("2 CV · dưới 1 phút"), "ước tính 2 CV");
        await r.ToggleAsync(r.Items[1], false);
        await WaitUntil(() => r.Text.Contains("1 CV · dưới 1 phút"), "ước tính 1 CV");
        await r.ToggleAsync(r.Items[1], true);
        await Task.Delay(100);
        Assert.Contains("2 CV · dưới 1 phút", r.Text);
        Assert.Equal(1, api.CallCount("estimate?count=2"));
        Assert.Equal(1, api.CallCount("estimate?count=1"));
    }

    // ================= (5) Hộp "Chữ đã đọc" =================

    private static async Task<(R Renderer, Api Api, UploadItem Item)> WithSavedCvAsync(ServiceProvider services, Api api)
    {
        var r = await OpenAsync(services);
        await r.EnableUploadAsync();
        await r.DropAsync(new FakeFile("cv-anh-mo.png", 4096, contentType: "image/png"));
        return (r, api, Assert.Single(r.Items));
    }

    [Fact]
    public async Task Dialog_loads_text_only_when_opened_shows_five_metrics_and_only_one_dialog_at_a_time()
    {
        var api = new Api();
        api.Text = id => Task.FromResult(Json(TextDto(id, TextQualityLevels.Low, "Dong 1\nDong 2")));
        await using var services = Services(api);
        var (r, _, item) = await WithSavedCvAsync(services, api);
        await using var _ = r;
        Assert.Equal(0, api.CallCount("/text"));                     // chưa mở: chưa tải chữ

        await r.ViewTextAsync(item, "cv-menu-x");
        await WaitUntil(() => r.Text.Contains("Số ký tự"), "chỉ số chất lượng");
        var text = r.Text;
        foreach (var label in new[] { "Số ký tự", "Số từ", "Tỷ lệ chữ cái", "Phương pháp trích xuất", "Đánh giá" }) Assert.Contains(label, text);
        Assert.Contains("OCR từ ảnh", text);
        Assert.Contains("Có thể đọc sai", text);
        Assert.Contains("Chỉ đọc được 13 ký tự", text);
        Assert.Contains("Dong 1", text);
        Assert.Contains("cv-anh-mo.png", text);                          // tên file HR đã chọn
        Assert.DoesNotContain("ten-luu-tren-may-chu", text);             // không dùng tên file lưu trên máy chủ
        Assert.Contains("Mở file gốc", text);
        Assert.Contains("Sao chép chữ", text);
        Assert.Equal(TextCheck.Done, item.TextCheck);                    // dòng nhận trạng thái + chất lượng từ hộp
        Assert.True(item.MaybeMisread);

        await r.ViewTextAsync(item);                                     // mở lần nữa khi đang mở: bỏ qua
        Assert.Equal(1, api.CallCount("/text"));
        await await r.ClickAsync("Đóng");
        Assert.Null(r.Dialog);
    }

    [Theory]
    [InlineData(410, "Hồ sơ đã được ẩn danh hóa", false)]
    [InlineData(404, "Không tìm thấy CV", false)]
    [InlineData(503, "Máy chủ API đang gặp lỗi", true)]
    public async Task Dialog_reports_anonymized_missing_and_server_errors_without_old_data(int status, string title, bool canRetry)
    {
        var api = new Api { Text = _ => Task.FromResult(Problem(status, "x")) };
        await using var services = Services(api);
        var (r, _, item) = await WithSavedCvAsync(services, api);
        await using var _ = r;

        await r.ViewTextAsync(item);
        await WaitUntil(() => r.Text.Contains(title), "thông báo lỗi");
        Assert.Equal(canRetry, r.Text.Contains("Thử lại"));
        Assert.DoesNotContain("Số ký tự", r.Text);
        if (!canRetry) Assert.DoesNotContain("Mở file gốc", r.Text.Split(title)[1]);   // CV không còn / đã ẩn danh: không mời mở file gốc
        if (status == 410)
        {
            Assert.Equal(TextCheck.Anonymized, item.TextCheck);
            await await r.ClickAsync("Đóng");
            Assert.Contains("Hồ sơ đã được ẩn danh hóa", r.Text);       // dòng trong bảng cũng đổi
        }
    }

    [Fact]
    public async Task Dialog_offline_then_retry_and_pending_state()
    {
        var api = new Api();
        var attempt = 0;
        api.Text = id => ++attempt switch
        {
            1 => throw new HttpRequestException("Connection refused"),
            2 => Task.FromResult(Json(new CvTextDto { CvId = id, ExtractionStatus = ExtractionStatuses.Pending, StatusMessage = "CV đang chờ đọc chữ." })),
            _ => Task.FromResult(Json(TextDto(id, TextQualityLevels.Good, new string('a', 900))))
        };
        await using var services = Services(api);
        var (r, _, item) = await WithSavedCvAsync(services, api);
        await using var _ = r;

        await r.ViewTextAsync(item);
        await WaitUntil(() => r.Text.Contains("Không kết nối được máy chủ API"), "mất kết nối");
        Assert.DoesNotContain("Connection refused", r.Text);
        await await r.ClickAsync("Thử lại");
        await WaitUntil(() => r.Text.Contains("Hệ thống đang đọc chữ từ file này"), "đang đọc");
        await await r.ClickAsync("Tải lại");
        await WaitUntil(() => r.Text.Contains("Chữ đọc rõ."), "đọc tốt");
        Assert.Equal(3, api.CallCount("/text"));
        Assert.Equal(TextCheck.Done, item.TextCheck);
    }

    [Fact]
    public async Task Anonymous_mode_hides_full_text_file_link_and_old_file_names_everywhere()
    {
        var anonymity = new AnonymityState();                          // chưa đọc được lựa chọn: luôn che (B36 fail-closed)
        var api = new Api();
        const string pii = "NGUYEN VAN A 0901 234 567 a@example.test";
        api.Text = id => Task.FromResult(Json(TextDto(id, TextQualityLevels.Good, pii + new string('x', 600))));
        api.Upload = u => Task.FromResult(Json(new CvUploadResponse
        {
            Files = [new() { FileName = u.Files[0], Status = CvUploadStatuses.Accepted, CvId = api.IdOf(u.Files[0]) },
                     new() { FileName = u.Files[1], Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate,
                             CvId = Guid.NewGuid(), ErrorMessage = "Trùng với CV đã tải trước đó: \"Nguyen-Van-B.pdf\"." }]
        }));
        await using var services = Services(api, anonymity);
        await using var r = await OpenAsync(services);
        await r.EnableUploadAsync();
        await r.DropAsync(new FakeFile("moi.pdf"), new FakeFile("cu.pdf"));

        var text = r.Text;
        Assert.DoesNotContain("Nguyen-Van-B", text);                    // câu trùng kiểu cũ có tên file hồ sơ khác
        Assert.DoesNotContain(r.AttributeValues("title"), t => t?.Contains("Nguyen-Van-B") == true);
        Assert.Contains("CV này đã có trong hệ thống.", text);

        var item = r.Items.Single(i => i.FileName == "moi.pdf");
        await r.ClickIdAsync($"cv-menu-{item.LocalId:N}");
        Assert.Contains("Tắt Ẩn danh để mở", r.Text);
        Assert.DoesNotContain(r.AttributeValues("href"), h => h?.Contains("/file") == true);
        await r.ClickIdAsync($"cv-menu-{item.LocalId:N}");

        await r.ViewTextAsync(item);
        await WaitUntil(() => r.Text.Contains("Toàn văn CV đang được ẩn"), "hộp che toàn văn");
        Assert.Contains("Số ký tự", r.Text);
        Assert.DoesNotContain("NGUYEN VAN A", r.Text);
        Assert.DoesNotContain(r.AttributeValues("title").Concat(r.AttributeValues("aria-label")), v => v?.Contains("NGUYEN") == true);
        Assert.DoesNotContain("Mở file gốc", r.Text);
        Assert.DoesNotContain("Sao chép chữ", r.Text);

        await r.Dispatcher.InvokeAsync(() => anonymity.SetAsync(false));   // HR tắt Ẩn danh (công tắc B36 trong hộp)
        await WaitUntil(() => r.Text.Contains("NGUYEN VAN A"), "tắt Ẩn danh thì tải lại toàn văn");
        Assert.Equal(2, api.CallCount("/text"));
        await r.Dispatcher.InvokeAsync(() => anonymity.SetAsync(true));    // bật lại: bỏ ngay toàn văn khỏi giao diện
        await WaitUntil(() => !r.Text.Contains("NGUYEN VAN A"), "bật lại Ẩn danh");
        Assert.Contains("Toàn văn CV đang được ẩn", r.Text);
    }
}
#pragma warning restore BL0006
