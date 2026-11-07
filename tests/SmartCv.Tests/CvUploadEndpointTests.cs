using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;

namespace SmartCv.Tests;

/// <summary>
/// A4 (bảo mật): kiểm thử API POST /api/cvs/upload qua toàn bộ pipeline HTTP (TestServer, trong bộ nhớ).
/// Dùng cấu hình GIẢ cho chuỗi kết nối và Gemini key: không kết nối SQL Server, không gọi Gemini.
/// Giới hạn dung lượng 413 phụ thuộc máy chủ Kestrel nên được kiểm thử thủ công bằng curl (xem README mục 13.4).
/// </summary>
public class CvUploadEndpointTests
{
    private const long MaxRequestBytes = 201L * 1024 * 1024;

    private static WebApplicationFactory<Program> CreateFactory(
        string environment = "Development", Dictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            // Đọc sớm trong AddInfrastructure nên phải dùng UseSetting. Giá trị giả, không dùng thật.
            builder.UseSetting("ConnectionStrings:SmartCvDb", "Server=(local);Database=SmartCv_Test_NotUsed;Trusted_Connection=True");
            builder.UseSetting("Gemini:ApiKey", "test-not-a-real-key");
            if (settings is not null)
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            }
        });

    private static MultipartFormDataContent Files(params string[] names)
    {
        var content = new MultipartFormDataContent();
        foreach (var name in names)
        {
            var part = new ByteArrayContent(new byte[100]);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Add(part, "files", name);
        }
        return content;
    }

    [Fact]
    public async Task Development_upload_returns_received()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/cvs/upload", Files("cv-mau-1.pdf", "cv-mau-2.pdf"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CvUploadResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.TotalFiles);
        Assert.All(body.Files, f => Assert.Equal(CvUploadStatuses.Received, f.Status));
    }

    [Fact]
    public async Task No_file_returns_400_problem()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var content = new MultipartFormDataContent { { new StringContent("khong co file"), "note" } };

        using var response = await client.PostAsync("/api/cvs/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Chưa chọn file", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task More_than_20_files_returns_400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/cvs/upload",
            Files(Enumerable.Range(1, 21).Select(i => $"cv-{i}.pdf").ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Quá nhiều file", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Request_without_antiforgery_token_is_accepted()
    {
        // Kiến trúc đã chọn: chưa có cookie đăng nhập nên không dùng antiforgery; client hợp lệ (Blazor phía
        // máy chủ, curl) gửi multipart bình thường. Truy cập được kiểm soát bằng việc chỉ bật ở Development.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cvs/upload") { Content = Files("cv.pdf") };
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Production_default_does_not_expose_upload_endpoint()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/cvs/upload", Files("cv.pdf"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(EndpointsOf(factory), e => e.RoutePattern.RawText == "/api/cvs/upload");
    }

    [Fact]
    public void Production_with_endpoint_enabled_fails_at_startup()
    {
        using var factory = CreateFactory("Production", new() { ["CvUpload:Endpoint:Enabled"] = "true" });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Không được bật API tải CV", ex.ToString());
    }

    [Fact]
    public void Only_upload_endpoint_has_large_request_limit()
    {
        using var factory = CreateFactory();
        var endpoints = EndpointsOf(factory);

        var upload = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/api/cvs/upload");
        Assert.Equal(MaxRequestBytes, upload.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
        Assert.Equal(MaxRequestBytes, upload.Metadata.GetMetadata<IFormOptionsMetadata>()?.MultipartBodyLengthLimit);

        // Các endpoint khác (health check, OpenAPI) không có giới hạn riêng -> giữ mặc định của Kestrel (30 MB)
        var others = endpoints.Where(e => e != upload).ToList();
        Assert.NotEmpty(others);
        Assert.All(others, e => Assert.Null(e.Metadata.GetMetadata<IRequestSizeLimitMetadata>()));
    }

    [Fact]
    public async Task Exceeding_rate_limit_returns_429_problem_with_retry_after()
    {
        using var factory = CreateFactory(settings: new() { ["CvUpload:Endpoint:PermitLimit"] = "2" });
        using var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            using var ok = await client.PostAsync("/api/cvs/upload", Files("cv.pdf"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await client.PostAsync("/api/cvs/upload", Files("cv.pdf"));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Contains("Quá nhiều yêu cầu tải CV", await limited.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Other_endpoints_are_not_rate_limited()
    {
        using var factory = CreateFactory(settings: new() { ["CvUpload:Endpoint:PermitLimit"] = "1" });
        using var client = factory.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            using var response = await client.GetAsync("/openapi/v1.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static List<RouteEndpoint> EndpointsOf(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
}
