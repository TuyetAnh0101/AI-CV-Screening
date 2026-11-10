extern alias web;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B2: service gọi API của SmartCv.Web.
/// (1) ApiClientBase đổi lỗi HTTP / mất kết nối / quá thời gian thành câu tiếng Việt, không lộ chi tiết kỹ thuật.
/// (2) CvApiClient tương thích với API tải CV thật (A4, chạy trong bộ nhớ): đúng tên phần "files", đúng hợp đồng.
/// </summary>
public class WebApiClientTests
{
    // ---------- (1) Đổi lỗi thành thông báo tiếng Việt ----------

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    private static JobApiClient JobClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://api.test/"), Timeout = timeout ?? TimeSpan.FromSeconds(30) },
            NullLogger<JobApiClient>.Instance);

    private static HttpResponseMessage Status(HttpStatusCode code, string? json = null, string mediaType = "application/problem+json") =>
        new(code) { Content = json is null ? new ByteArrayContent([]) : new StringContent(json, Encoding.UTF8, mediaType) };

    [Fact]
    public async Task Success_returns_data()
    {
        var client = JobClient((_, _) => Task.FromResult(Status(HttpStatusCode.OK,
            """[{"jobId":1,"title":"Backend Developer (.NET)","skills":[]}]""", "application/json")));

        var result = await client.GetJobsAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Backend Developer (.NET)", Assert.Single(result.Data!).Title);
    }

    [Fact]
    public async Task Problem_details_detail_is_shown_to_user()
    {
        var client = JobClient((_, _) => Task.FromResult(Status(HttpStatusCode.BadRequest,
            """{"title":"Quá nhiều file","status":400,"detail":"Mỗi lần chỉ được tải tối đa 20 file, bạn đã gửi 21 file."}""")));

        var result = await client.GetJobsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("Mỗi lần chỉ được tải tối đa 20 file, bạn đã gửi 21 file.", result.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Dữ liệu gửi lên không hợp lệ.")]
    [InlineData(HttpStatusCode.NotFound, "Không tìm thấy dữ liệu hoặc API chưa có đường dẫn này.")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "Dữ liệu gửi lên quá lớn.")]
    [InlineData(HttpStatusCode.UnsupportedMediaType, "Định dạng dữ liệu gửi lên không được hỗ trợ.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Đang có quá nhiều yêu cầu. Vui lòng chờ một lát rồi thử lại.")]
    [InlineData(HttpStatusCode.InternalServerError, "Máy chủ API gặp lỗi. Xem log ở terminal đang chạy API.")]
    public async Task Status_without_body_has_vietnamese_message(HttpStatusCode code, string expected)
    {
        var client = JobClient((_, _) => Task.FromResult(Status(code)));

        var result = await client.GetJobsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal((int)code, result.StatusCode);
        Assert.Equal(expected, result.ErrorMessage);
    }

    [Fact]
    public async Task Server_error_body_is_not_shown_to_user()
    {
        // Nội dung lỗi không phải ProblemDetails (ví dụ trang lỗi có stack trace) không được đưa ra giao diện
        var client = JobClient((_, _) => Task.FromResult(Status(HttpStatusCode.InternalServerError,
            "System.InvalidOperationException: secret stack trace at SmartCv.Api...", "text/plain")));

        var result = await client.GetJobsAsync();

        Assert.Equal("Máy chủ API gặp lỗi. Xem log ở terminal đang chạy API.", result.ErrorMessage);
        Assert.DoesNotContain("stack", result.ErrorMessage);
    }

    [Fact]
    public async Task Connection_failure_has_vietnamese_message()
    {
        var client = JobClient((_, _) => throw new HttpRequestException("No connection could be made"));

        var result = await client.GetJobsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("Không kết nối được máy chủ API. Hãy kiểm tra API đã chạy và ApiBaseUrl đúng cổng.", result.ErrorMessage);
    }

    [Fact]
    public async Task Timeout_has_vietnamese_message()
    {
        var client = JobClient(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Status(HttpStatusCode.OK); },
            timeout: TimeSpan.FromMilliseconds(100));

        var result = await client.GetJobsAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("Máy chủ API phản hồi quá lâu. Vui lòng thử lại.", result.ErrorMessage);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_timeout()
    {
        using var cts = new CancellationTokenSource();
        var client = JobClient(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Status(HttpStatusCode.OK); });
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetJobsAsync(ct: cts.Token));
    }

    // ---------- (2) CvApiClient gọi API tải CV thật (A4) ----------

    private sealed class FakeBrowserFile(string name, string contentType, byte[] bytes) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => bytes.Length;
        public string ContentType => contentType;

        // Giống Blazor: đọc file lớn hơn maxAllowedSize thì báo lỗi
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            Size > maxAllowedSize ? throw new IOException("Supplied file exceeds the maximum") : new MemoryStream(bytes);
    }

    private static (WebApplicationFactory<Program> Api, CvApiClient Client) CvClientAgainstRealApi()
    {
        // API tải CV chỉ bật ở Development (A4); database InMemory (A5 tra trùng trong bảng Cvs)
        var api = TestSupport.ApiFactory();
        return (api, new CvApiClient(api.CreateClient(),
            Microsoft.Extensions.Options.Options.Create(new SmartCv.Core.Options.CvUploadOptions()), NullLogger<CvApiClient>.Instance));
    }

    [Fact]
    public async Task CvApiClient_upload_matches_A4_contract()
    {
        var (api, client) = CvClientAgainstRealApi();
        using var _ = api;

        // CV 1 MB > 500 KB mặc định của Blazor: chứng minh client đã truyền maxAllowedSize = 10 MB
        var result = await client.UploadAsync(
        [
            new FakeBrowserFile("cv-mau-1.pdf", "application/pdf", TestSupport.Pdf("cv-mau-1", 1024 * 1024)),
            new FakeBrowserFile("cv-mau-2.png", "image/png", TestSupport.Png("cv-mau-2")),
        ]);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(2, result.Data!.TotalFiles);
        Assert.Equal(["cv-mau-1.pdf", "cv-mau-2.png"], result.Data.Files.Select(f => f.FileName));
        Assert.Equal(1024 * 1024, result.Data.Files[0].SizeBytes);
        Assert.All(result.Data.Files, f => { Assert.Equal(CvUploadStatuses.Accepted, f.Status); Assert.NotNull(f.CvId); });   // A6: đã lưu
    }

    [Fact]
    public async Task CvApiClient_receives_A5_rejections_per_file()
    {
        // B5 dựa vào status, errorCode, errorMessage của từng file; HTTP vẫn 200 nên IsSuccess = true
        var (api, client) = CvClientAgainstRealApi();
        using var _ = api;

        var result = await client.UploadAsync(
        [
            new FakeBrowserFile("cv.pdf", "application/pdf", TestSupport.Pdf("hop-le")),
            new FakeBrowserFile("gia-mao.pdf", "application/pdf", "Day khong phai CV"u8.ToArray()),
        ]);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal((2, 1, 1), (result.Data!.TotalFiles, result.Data.SuccessCount, result.Data.FailedCount));
        Assert.Equal(CvUploadStatuses.Rejected, result.Data.Files[1].Status);
        Assert.Equal(CvUploadErrorCodes.InvalidFormat, result.Data.Files[1].ErrorCode);
        Assert.Contains("Nội dung không phải file PDF", result.Data.Files[1].ErrorMessage);
    }

    [Fact]
    public async Task CvApiClient_shows_api_message_when_too_many_files()
    {
        var (api, client) = CvClientAgainstRealApi();
        using var _ = api;

        var files = Enumerable.Range(1, 21)
            .Select(i => (IBrowserFile)new FakeBrowserFile($"cv-{i}.pdf", "application/pdf", new byte[10]))
            .ToList();

        var result = await client.UploadAsync(files);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("Mỗi lần chỉ được tải tối đa 20 file, bạn đã gửi 21 file.", result.ErrorMessage);
    }

    // ---------- Sửa lỗi upload nhiều CV làm circuit Blazor Server chết ----------

    /// <summary>Ghi lại lúc mở / đóng stream để đếm số file đang truyền cùng lúc.</summary>
    private sealed class OpenTracker
    {
        public int Open, MaxOpen, TotalOpened;
        public void Opened() { TotalOpened++; Open++; MaxOpen = Math.Max(MaxOpen, Open); }
        public void Closed() => Open--;
    }

    private sealed class TrackedStream(byte[] bytes, OpenTracker tracker) : MemoryStream(bytes)
    {
        private bool closed;
        protected override void Dispose(bool disposing)
        {
            if (!closed) { closed = true; tracker.Closed(); }
            base.Dispose(disposing);
        }
    }

    private sealed class TrackedBrowserFile(string name, byte[] bytes, OpenTracker tracker) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => bytes.Length;
        public string ContentType => "application/pdf";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Size > maxAllowedSize) throw new IOException("Supplied file exceeds the maximum");
            tracker.Opened();
            return new TrackedStream(bytes, tracker);
        }
    }

    [Fact]
    public async Task CvApiClient_opens_browser_files_one_at_a_time_and_only_while_sending()
    {
        // Trên Blazor Server mỗi OpenReadStream mở ngay một luồng dữ liệu qua circuit. Mở trước cả nhóm
        // làm circuit nghẽn và chết sau 1 phút (TimeoutException "Did not receive any data in the allotted time").
        var tracker = new OpenTracker();
        int? openedBeforeSend = null;
        long? contentLength = null;
        var http = new HttpClient(new StubHandler(async (request, ct) =>
        {
            openedBeforeSend = tracker.TotalOpened;
            contentLength = request.Content!.Headers.ContentLength;
            await request.Content.CopyToAsync(Stream.Null, ct);   // giống HttpClient thật: gửi lần lượt từng phần
            return Status(HttpStatusCode.OK, """{"totalFiles":0,"successCount":0,"failedCount":0,"files":[]}""", "application/json");
        })) { BaseAddress = new Uri("https://api.test/") };
        var client = new CvApiClient(http, Microsoft.Extensions.Options.Options.Create(new SmartCv.Core.Options.CvUploadOptions()),
            NullLogger<CvApiClient>.Instance);
        var files = Enumerable.Range(1, 20)
            .Select(i => (IBrowserFile)new TrackedBrowserFile($"cv-{i}.pdf", TestSupport.Pdf($"cv-{i}", 64 * 1024), tracker))
            .ToList();

        var result = await client.UploadAsync(files);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(0, openedBeforeSend);        // chưa file nào mở khi bắt đầu gửi
        Assert.Equal(1, tracker.MaxOpen);         // mỗi lúc chỉ một file đang truyền
        Assert.Equal(20, tracker.TotalOpened);    // mỗi file mở đúng một lần
        Assert.Equal(0, tracker.Open);            // gửi xong thì mọi stream đã đóng
        Assert.NotNull(contentLength);            // biết trước dung lượng: có Content-Length, không gửi chunked
    }

    [Fact]
    public async Task CvApiClient_still_rejects_file_larger_than_limit_when_sending()
    {
        var tracker = new OpenTracker();
        var http = new HttpClient(new StubHandler(async (request, ct) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, ct);
            return Status(HttpStatusCode.OK, """{"files":[]}""", "application/json");
        })) { BaseAddress = new Uri("https://api.test/") };
        var client = new CvApiClient(http, Microsoft.Extensions.Options.Options.Create(new SmartCv.Core.Options.CvUploadOptions { MaxFileSizeMB = 1 }),
            NullLogger<CvApiClient>.Instance);

        // Trang Upload lọc file quá lớn trước khi gửi; nếu lọt xuống đây, việc đọc vẫn bị chặn.
        // HttpClient bọc lỗi đọc nội dung thành HttpRequestException → ApiClientBase trả kết quả lỗi, không ném ra circuit.
        var result = await client.UploadAsync([new TrackedBrowserFile("qua-lon.pdf", new byte[2 * 1024 * 1024], tracker)]);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, tracker.TotalOpened);   // bị chặn trước khi mở stream
    }
}
