extern alias web;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Models;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B5: kiểm tra file trên giao diện (lớp 1), xử lý lỗi API trả về (lớp 2) và hiển thị trên trang Tải lên.
/// </summary>
#pragma warning disable BL0006 // RenderTree APIs chỉ dùng trong renderer kiểm thử.
public class UploadValidationTests
{
    private const long MB = 1024 * 1024;
    private static readonly CvUploadOptions Limits = new();   // mặc định: 10 MB, PDF/PNG/JPG/JPEG

    // ================= (1) UploadValidator =================

    private static UploadValidator Validator(int maxPerSelection = 25) => new(Limits, maxPerSelection);

    [Theory]
    [InlineData("cv-word.docx", 1000, "Sai định dạng \".docx\". Chỉ nhận PDF, PNG, JPG.")]
    [InlineData("cv-khong-duoi", 1000, "Sai định dạng. Chỉ nhận PDF, PNG, JPG.")]
    [InlineData("cv.", 1000, "Sai định dạng. Chỉ nhận PDF, PNG, JPG.")]
    [InlineData("file-rong.pdf", 0, "File rỗng (0 byte).")]
    [InlineData("qua-lon-12MB.pdf", 12 * MB, "File nặng 12,0 MB, vượt giới hạn 10 MB.")]
    public void Rejects_bad_files_with_vietnamese_reason(string name, long size, string expected) =>
        Assert.Equal(expected, Validator().Validate(name, size, [], 0));

    [Theory]
    [InlineData("cv.pdf")]
    [InlineData("CV.PDF")]
    [InlineData("anh.jpeg")]
    [InlineData("anh.JPG")]
    [InlineData("anh.png")]
    public void Accepts_supported_formats_case_insensitively(string name) =>
        Assert.Null(Validator().Validate(name, 10 * MB, [], 0));   // đúng bằng giới hạn vẫn hợp lệ

    [Fact]
    public void Same_file_already_in_list_is_duplicate_but_errored_rows_do_not_block_retry()
    {
        var uploaded = new UploadItem { FileName = "cv.pdf", SizeBytes = 500, State = UploadItemState.Uploaded };
        var failed = new UploadItem { FileName = "retry.pdf", SizeBytes = 500, State = UploadItemState.Rejected };

        Assert.Equal("File này đã có trong danh sách.", Validator().Validate("CV.pdf", 500, [uploaded], 0));
        Assert.Null(Validator().Validate("cv.pdf", 501, [uploaded], 0));      // khác dung lượng: file khác
        Assert.Null(Validator().Validate("retry.pdf", 500, [failed], 0));     // dòng lỗi: cho thả lại
    }

    [Fact]
    public void Over_selection_limit_is_rejected_and_only_valid_files_take_a_slot()
    {
        var v = Validator(maxPerSelection: 2);
        Assert.Null(v.Validate("a.pdf", 1, [], acceptedInSelection: 1));
        Assert.Equal("Vượt quá 2 CV mỗi lần tải. Hãy tải file này ở lần sau.", v.Validate("c.pdf", 1, [], acceptedInSelection: 2));
        // File sai định dạng báo đúng lý do của nó, không bị báo "vượt quá"
        Assert.StartsWith("Sai định dạng", v.Validate("c.docx", 1, [], acceptedInSelection: 2));
    }

    // ================= (2) UploadFlow: lỗi API =================

    private static UploadItem Sending(string name) => new() { FileName = name, State = UploadItemState.Uploading };

    [Fact]
    public void Api_content_errors_become_rejected_with_api_message_and_code()
    {
        var fake = Sending("gia-mao-pdf.pdf");
        var noMessage = Sending("x.pdf");
        var response = new CvUploadResponse
        {
            Files =
            [
                new() { FileName = "gia-mao-pdf.pdf", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.InvalidFormat,
                        ErrorMessage = "Nội dung không phải file PDF hợp lệ (có thể bị đổi đuôi hoặc bị hỏng)." },
                new() { FileName = "x.pdf", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.SaveFailed },
            ]
        };

        UploadFlow.ApplyResult([fake, noMessage], ApiResult<CvUploadResponse>.Success(response));

        Assert.Equal((UploadItemState.Rejected, CvUploadErrorCodes.InvalidFormat, false), (fake.State, fake.ErrorCode, fake.Selected));
        Assert.Contains("không phải file PDF hợp lệ", fake.Message);
        Assert.Equal("Máy chủ chưa lưu được file. Vui lòng thử lại.", noMessage.Message);
        Assert.Equal("Bị từ chối", StatusText.ForUpload(fake.State).Text);
        Assert.Equal("Lỗi file", StatusText.ForUpload(UploadItemState.Invalid).Text);
    }

    [Fact]
    public void Duplicate_with_old_cvId_is_existing_and_selectable_without_cvId_is_not()
    {
        var withId = Sending("cv-mau-1.pdf");
        var withoutId = Sending("cv-mau-1-ban-sao.pdf");
        var oldId = Guid.NewGuid();
        var response = new CvUploadResponse
        {
            Files =
            [
                new() { FileName = "cv-mau-1.pdf", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate, CvId = oldId, ErrorMessage = "Trùng với CV đã tải trước đó." },
                new() { FileName = "cv-mau-1-ban-sao.pdf", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate, ErrorMessage = "Trùng với CV đã tải trước đó." },
            ]
        };

        UploadFlow.ApplyResult([withId, withoutId], ApiResult<CvUploadResponse>.Success(response));

        Assert.Equal((UploadItemState.Existing, oldId, true), (withId.State, withId.CvId, withId.CanAnalyze));
        Assert.Equal("Đã có sẵn", StatusText.ForUpload(withId.State).Text);
        Assert.Null(UploadFlow.RejectedHint(withId));

        Assert.Equal((UploadItemState.Rejected, (Guid?)null, false), (withoutId.State, withoutId.CvId, withoutId.CanAnalyze));
        Assert.NotNull(UploadFlow.RejectedHint(withoutId));   // nói rõ vì sao chưa phân tích được, không tạo ID giả
    }

    [Fact]
    public void Server_error_does_not_show_technical_detail()
    {
        var a = Sending("a.pdf");
        var result = ApiResult<CvUploadResponse>.Failure("System.NullReferenceException at SmartCv...", 500);

        UploadFlow.ApplyResult([a], result);

        Assert.Equal(UploadFlow.ServerErrorRowMessage, a.Message);
        var banner = UploadFlow.DescribeBatchFailure(result)!.Value;
        Assert.DoesNotContain("Exception", banner.Message);
        Assert.Equal("Máy chủ API đang gặp lỗi", banner.Title);
    }

    [Fact]
    public void Batch_failure_banner_distinguishes_connection_and_rejection()
    {
        Assert.Null(UploadFlow.DescribeBatchFailure(ApiResult<CvUploadResponse>.Success(new CvUploadResponse())));
        Assert.Equal("Không kết nối được máy chủ API",
            UploadFlow.DescribeBatchFailure(ApiResult<CvUploadResponse>.Failure("Máy chủ API phản hồi quá lâu."))!.Value.Title);
        var rejected = UploadFlow.DescribeBatchFailure(ApiResult<CvUploadResponse>.Failure("Mỗi lần chỉ được tải tối đa 20 file.", 400))!.Value;
        Assert.Equal(("API từ chối yêu cầu tải lên", "Mỗi lần chỉ được tải tối đa 20 file."), rejected);
    }

    [Fact]
    public void Summary_counts_new_existing_rejected_and_invalid()
    {
        var sent = new[]
        {
            new UploadItem { FileName = "a", State = UploadItemState.Uploaded },
            new UploadItem { FileName = "b", State = UploadItemState.Uploaded },
            new UploadItem { FileName = "c", State = UploadItemState.Existing },
            new UploadItem { FileName = "d", State = UploadItemState.Rejected },
        };

        var (text, hasErrors) = UploadFlow.Summarize(sent, invalidCount: 3);
        Assert.StartsWith("Kết quả tải lên: 2 CV mới, 1 CV đã có sẵn, 1 file bị từ chối, 3 file lỗi chưa gửi.", text);
        Assert.True(hasErrors);

        Assert.Equal(("Kết quả tải lên: 1 CV mới.", false), UploadFlow.Summarize([sent[0]], 0));
        Assert.True(UploadFlow.Summarize([], 2).HasErrors);
    }

    // ================= (3) Trang Upload: hiển thị và luồng thật =================

    private sealed class FakeFile(string name, long size) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => size;
        public string ContentType => "application/pdf";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new MemoryStream(new byte[Math.Min(size, 64)]);   // nội dung không quan trọng: API là giả lập
    }

    /// <summary>API giả: /api/jobs trả rỗng; /api/cvs/upload ghi lại tên file nhận được và trả kết quả do test quyết định.</summary>
    private sealed class FakeApi(Func<IReadOnlyList<string>, HttpResponseMessage> upload) : HttpMessageHandler
    {
        public List<string> UploadedNames { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/jobs")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<JobDto>()) });

            Assert.Equal("/api/cvs/upload", request.RequestUri.AbsolutePath);
            var names = ((MultipartFormDataContent)request.Content!)
                .Select(p => p.Headers.ContentDisposition!.FileName!.Trim('"')).ToList();
            UploadedNames.AddRange(names);
            return Task.FromResult(upload(names));
        }
    }

    private static HttpResponseMessage Ok(params CvUploadItemResult[] files) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new CvUploadResponse { TotalFiles = files.Length, Files = [.. files] }) };

    private static CvUploadItemResult Accepted(string name) => new() { FileName = name, Status = CvUploadStatuses.Accepted, CvId = Guid.NewGuid() };

    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        private int rootId;

        public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
        {
            rootId = AssignRootComponentId(InstantiateComponent(typeof(Upload)));
            await RenderRootComponentAsync(rootId);
        });

        public List<RenderTreeFrame> Frames(int? id = null)
        {
            var frames = GetCurrentRenderTreeFrames(id ?? rootId);
            return frames.Array.Take(frames.Count).SelectMany(f => f.FrameType == RenderTreeFrameType.Component
                ? new[] { f }.Concat(Frames(f.ComponentId)) : [f]).ToList();
        }

        public string Text => System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(string.Concat(
            Frames().Select(f => f.FrameType switch
            {
                RenderTreeFrameType.Text => f.TextContent,
                RenderTreeFrameType.Markup => System.Text.RegularExpressions.Regex.Replace(f.MarkupContent, "<[^>]+>", " "),
                _ => " "
            }))), @"\s+", " ");

        public Task DropAsync(params IBrowserFile[] files) => Dispatcher.InvokeAsync(() =>
        {
            var callback = (EventCallback<InputFileChangeEventArgs>)Frames()
                .First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "OnFilesSelected").AttributeValue;
            return callback.InvokeAsync(new InputFileChangeEventArgs(files));
        });

        /// <summary>Các thuộc tính của thẻ input có aria-label cho trước (ô tích của một dòng).</summary>
        public Dictionary<string, object?> InputAttributes(string ariaLabel)
        {
            var frames = Frames();
            var label = frames.FindIndex(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-label"
                                              && Equals(f.AttributeValue, ariaLabel));
            Assert.True(label >= 0, $"Không thấy ô có aria-label \"{ariaLabel}\"");
            var element = frames.Take(label).ToList().FindLastIndex(f => f.FrameType == RenderTreeFrameType.Element);
            return frames.Skip(element + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute)
                .GroupBy(f => f.AttributeName).ToDictionary(g => g.Key, g => (object?)g.Last().AttributeValue);
        }

        public Task ClickButtonWithTextAsync(string text) => Dispatcher.InvokeAsync(async () =>
        {
            var frames = Frames();
            var index = frames.FindIndex(f => (f.FrameType == RenderTreeFrameType.Text && f.TextContent.Contains(text))
                                              || (f.FrameType == RenderTreeFrameType.Markup && f.MarkupContent.Contains(text)));
            Assert.True(index >= 0, $"Không thấy nút \"{text}\"");
            var handler = frames.Take(index).Last(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick");
            await DispatchEventAsync(handler.AttributeEventHandlerId, null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        });

        public Task ChangeInputAsync(string ariaLabel, bool value) => Dispatcher.InvokeAsync(() =>
        {
            var frames = Frames();
            var label = frames.FindIndex(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-label"
                                              && Equals(f.AttributeValue, ariaLabel));
            var handler = frames.Skip(label).First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onchange");
            return DispatchEventAsync(handler.AttributeEventHandlerId, null, new ChangeEventArgs { Value = value });
        });
    }

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    private static ServiceProvider Services(HttpMessageHandler api, int maxFilesPerSelection = 25)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiBaseUrl"] = "https://api.test/",
            ["CvUpload:MaxFilesPerSelection"] = maxFilesPerSelection.ToString()
        }).Build());
        services.Configure<CvUploadOptions>(_ => { });
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>();
        var http = new HttpClient(api) { BaseAddress = new Uri("https://api.test/") };
        services.AddSingleton(new JobApiClient(http, NullLogger<JobApiClient>.Instance));
        services.AddSingleton(new CvApiClient(http, Microsoft.Extensions.Options.Options.Create(new CvUploadOptions()), NullLogger<CvApiClient>.Instance));
        // B6: trang Upload inject thêm 3 service này (test B5 không gọi tới)
        services.AddSingleton(new ApplicationApiClient(http, NullLogger<ApplicationApiClient>.Instance));
        services.AddSingleton(new RunApiClient(http, NullLogger<RunApiClient>.Instance));
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Invalid_files_get_own_row_reason_and_locked_checkbox_and_are_never_sent()
    {
        var api = new FakeApi(names => Ok(names.Select(Accepted).ToArray()));
        using var services = Services(api);
        await using var renderer = new TestRenderer(services);
        await renderer.StartAsync();

        await renderer.DropAsync(
            new FakeFile("cv-word.docx", 2000),
            new FakeFile("cv-khong-duoi", 2000),
            new FakeFile("file-rong.pdf", 0),
            new FakeFile("qua-lon-12MB.pdf", 12 * MB),
            new FakeFile("cv-mau-1.pdf", 5000),
            new FakeFile("cv-mau-1.pdf", 5000));   // thả trùng trong cùng lần

        Assert.Equal(["cv-mau-1.pdf"], api.UploadedNames);   // chỉ file hợp lệ được gửi, và chỉ một lần
        var text = renderer.Text;
        Assert.Contains("Sai định dạng \".docx\". Chỉ nhận PDF, PNG, JPG.", text);
        Assert.Contains("Sai định dạng. Chỉ nhận PDF, PNG, JPG.", text);
        Assert.Contains("File rỗng (0 byte).", text);
        Assert.Contains("File nặng 12,0 MB, vượt giới hạn 10 MB.", text);
        Assert.Contains("File này đã có trong danh sách.", text);
        Assert.Contains("Lỗi file", text);
        Assert.Contains("Hoàn thành", text);
        Assert.Contains("Lỗi 5", text);
        Assert.Contains("Kết quả tải lên: 1 CV mới, 5 file lỗi chưa gửi.", text);
        Assert.True(renderer.InputAttributes("Chọn cv-word.docx").ContainsKey("disabled"));
        Assert.False(renderer.InputAttributes("Chọn cv-mau-1.pdf").ContainsKey("disabled"));

        // Xóa N file lỗi: chỉ còn CV hợp lệ, không gọi thêm API
        await renderer.ClickButtonWithTextAsync("file lỗi khỏi danh sách");
        Assert.DoesNotContain("Lỗi file", renderer.Text);
        Assert.Contains("cv-mau-1.pdf", renderer.Text);
        Assert.Single(api.UploadedNames);
    }

    [Fact]
    public async Task Over_limit_files_are_marked_and_not_sent()
    {
        var api = new FakeApi(names => Ok(names.Select(Accepted).ToArray()));
        using var services = Services(api, maxFilesPerSelection: 2);
        await using var renderer = new TestRenderer(services);
        await renderer.StartAsync();

        await renderer.DropAsync(new FakeFile("a.pdf", 10), new FakeFile("b.pdf", 10), new FakeFile("c.pdf", 10));

        Assert.Equal(["a.pdf", "b.pdf"], api.UploadedNames);
        Assert.Contains("Vượt quá 2 CV mỗi lần tải. Hãy tải file này ở lần sau.", renderer.Text);
    }

    [Fact]
    public async Task Api_rejection_existing_cv_and_select_all()
    {
        var oldId = Guid.NewGuid();
        var api = new FakeApi(_ => Ok(
            new CvUploadItemResult { FileName = "gia-mao-pdf.pdf", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.InvalidFormat,
                                     ErrorMessage = "Nội dung không phải file PDF hợp lệ (có thể bị đổi đuôi hoặc bị hỏng)." },
            new CvUploadItemResult { FileName = "cv-mau-1.pdf", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate,
                                     CvId = oldId, ErrorMessage = "Trùng với CV đã tải trước đó: \"cv-mau-1.pdf\"." },
            new CvUploadItemResult { FileName = "cv-mau-3.pdf", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate,
                                     ErrorMessage = "Trùng với CV đã tải trước đó: \"cv-mau-3.pdf\"." },
            Accepted("moi.pdf")));
        using var services = Services(api);
        await using var renderer = new TestRenderer(services);
        await renderer.StartAsync();

        await renderer.DropAsync(new FakeFile("gia-mao-pdf.pdf", 10), new FakeFile("cv-mau-1.pdf", 10),
                                 new FakeFile("cv-mau-3.pdf", 10), new FakeFile("moi.pdf", 10));

        var text = renderer.Text;
        Assert.Contains("Bị từ chối", text);
        Assert.Contains("không phải file PDF hợp lệ", text);
        Assert.Contains("Đã có sẵn", text);
        Assert.Contains("Vẫn chọn được để phân tích.", text);
        Assert.Contains("Hệ thống chưa trả mã CV cũ", text);
        Assert.Contains("Kết quả tải lên: 1 CV mới, 1 CV đã có sẵn, 2 file bị từ chối.", text);
        Assert.Contains("Sẵn sàng 2 • Đã chọn 2", text);
        Assert.True(renderer.InputAttributes("Chọn gia-mao-pdf.pdf").ContainsKey("disabled"));
        Assert.True(renderer.InputAttributes("Chọn cv-mau-3.pdf").ContainsKey("disabled"));
        Assert.False(renderer.InputAttributes("Chọn cv-mau-1.pdf").ContainsKey("disabled"));

        // Ô "chọn tất cả" chỉ tác động lên CV phân tích được
        await renderer.ChangeInputAsync("Chọn tất cả CV đã tải lên", false);
        Assert.Contains("Đã chọn 0", renderer.Text);
        await renderer.ChangeInputAsync("Chọn tất cả CV đã tải lên", true);
        Assert.Contains("Sẵn sàng 2 • Đã chọn 2", renderer.Text);
    }

    [Fact]
    public async Task Lost_api_connection_shows_banner_and_rejected_rows()
    {
        var api = new FakeApi(_ => throw new HttpRequestException("Connection refused (localhost:7001)"));
        using var services = Services(api);
        await using var renderer = new TestRenderer(services);
        await renderer.StartAsync();

        await renderer.DropAsync(new FakeFile("a.pdf", 10));

        var text = renderer.Text;
        Assert.Contains("Không kết nối được máy chủ API", text);
        Assert.Contains("Bị từ chối", text);
        Assert.DoesNotContain("Connection refused", text);   // không lộ lỗi kỹ thuật
        Assert.Contains("Kết quả tải lên: 0 CV mới, 1 file bị từ chối.", text);

        // Thả lại đúng file đó sau khi API chạy lại: không bị chặn là "trùng trong danh sách"
        Assert.Null(new UploadValidator(Limits, 25).Validate("a.pdf", 10,
            [new UploadItem { FileName = "a.pdf", SizeBytes = 10, State = UploadItemState.Rejected }], 0));
    }
}
#pragma warning restore BL0006
