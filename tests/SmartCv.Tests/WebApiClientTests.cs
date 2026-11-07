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
        var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");   // API tải CV chỉ bật ở Development (A4)
            b.UseSetting("ConnectionStrings:SmartCvDb", "Server=(local);Database=SmartCv_Test_NotUsed;Trusted_Connection=True");
            b.UseSetting("Gemini:ApiKey", "test-not-a-real-key");
        });
        return (api, new CvApiClient(api.CreateClient(), NullLogger<CvApiClient>.Instance));
    }

    [Fact]
    public async Task CvApiClient_upload_matches_A4_contract()
    {
        var (api, client) = CvClientAgainstRealApi();
        using var _ = api;

        // CV 1 MB > 500 KB mặc định của Blazor: chứng minh client đã truyền maxAllowedSize = 10 MB
        var result = await client.UploadAsync(
        [
            new FakeBrowserFile("cv-mau-1.pdf", "application/pdf", new byte[1024 * 1024]),
            new FakeBrowserFile("cv-mau-2.png", "image/png", new byte[100]),
        ]);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(2, result.Data!.TotalFiles);
        Assert.Equal(["cv-mau-1.pdf", "cv-mau-2.png"], result.Data.Files.Select(f => f.FileName));
        Assert.Equal(1024 * 1024, result.Data.Files[0].SizeBytes);
        Assert.All(result.Data.Files, f => Assert.Equal(CvUploadStatuses.Received, f.Status));
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

    [Fact]
    public void CvApiClient_limit_matches_api_option()
    {
        Assert.Equal(CvApiClient.MaxFileSizeBytes, new SmartCv.Core.Options.CvUploadOptions().MaxFileSizeBytes);
    }
}
