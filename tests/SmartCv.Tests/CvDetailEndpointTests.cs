using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>A6: GET /api/cvs/{id} và GET /api/cvs/{id}/file qua pipeline HTTP (TestServer, database InMemory, thư mục tạm).</summary>
public class CvDetailEndpointTests
{
    private static async Task<(HttpClient Client, Guid CvId, byte[] Bytes)> UploadOneAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string name)
    {
        var client = factory.CreateClient();
        var bytes = TestSupport.Pdf(name + Guid.NewGuid());
        using var content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "files", name } };
        using var response = await client.PostAsync("/api/cvs/upload", content);
        var body = await response.Content.ReadFromJsonAsync<CvUploadResponse>();
        return (client, body!.Files[0].CvId!.Value, bytes);
    }

    [Fact]
    public async Task Get_detail_returns_saved_cv_without_internal_path()
    {
        using var factory = TestSupport.ApiFactory();
        var (client, cvId, bytes) = await UploadOneAsync(factory, "Nguyễn Văn An - CV.pdf");

        using var response = await client.GetAsync($"/api/cvs/{cvId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Uploads", json);     // không trả đường dẫn lưu trên máy chủ
        var dto = await response.Content.ReadFromJsonAsync<CvDetailDto>();
        Assert.Equal(cvId, dto!.CvId);
        Assert.Equal("Nguyễn Văn An - CV.pdf", dto.OriginalFileName);
        Assert.Equal(bytes.Length, dto.FileSizeBytes);
        Assert.Equal("application/pdf", dto.ContentType);
        Assert.Equal("Pending", dto.ExtractionStatus);
        Assert.True(dto.FileAvailable);
        Assert.Empty(dto.Applications);
        Assert.Equal(DateTimeKind.Utc, dto.UploadedAt.Kind);
    }

    [Fact]
    public async Task Get_file_inline_and_download_with_original_name()
    {
        using var factory = TestSupport.ApiFactory();
        var (client, cvId, bytes) = await UploadOneAsync(factory, "Nguyễn Văn An - CV.pdf");

        using var inline = await client.GetAsync($"/api/cvs/{cvId}/file");
        Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
        Assert.Equal("application/pdf", inline.Content.Headers.ContentType?.MediaType);
        Assert.Null(inline.Content.Headers.ContentDisposition);              // mở xem trong trình duyệt
        Assert.Equal("nosniff", inline.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(bytes, await inline.Content.ReadAsByteArrayAsync());

        using var download = await client.GetAsync($"/api/cvs/{cvId}/file?download=true");
        var disposition = download.Content.Headers.ContentDisposition;
        Assert.Equal("attachment", disposition?.DispositionType);
        Assert.Equal("Nguyễn Văn An - CV.pdf", disposition?.FileNameStar);   // tên gốc, mã hóa an toàn (RFC 5987)
    }

    [Fact]
    public async Task Unknown_cv_returns_404_for_detail_and_file()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cvs/{id}")).StatusCode);
        using var file = await client.GetAsync($"/api/cvs/{id}/file");
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
        Assert.Contains("Không tìm thấy CV", await file.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cv_in_database_without_physical_file_returns_404_for_file()
    {
        // Giống 12 CV demo: có dòng Cvs nhưng không có file trên máy chủ
        var dbName = Guid.NewGuid().ToString("N");
        using var factory = TestSupport.ApiFactory(dbName: dbName);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            db.Cvs.Add(new Cv
            {
                CvId = id, OriginalFileName = "CV_Demo.pdf", StoredPath = $"Uploads/CV/2026/11/{id}.pdf",
                ContentType = "application/pdf", FileSizeBytes = 100, FileHash = new string('A', 64),
                ExtractionStatus = "Completed", UploadedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var detail = await client.GetFromJsonAsync<CvDetailDto>($"/api/cvs/{id}");
        Assert.False(detail!.FileAvailable);

        using var file = await client.GetAsync($"/api/cvs/{id}/file");
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
        Assert.Contains("File gốc không còn trên máy chủ", await file.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Production_does_not_expose_cv_read_endpoints()
    {
        using var factory = TestSupport.ApiFactory("Production");
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cvs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cvs/{id}/file")).StatusCode);
        Assert.DoesNotContain(factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>(), e => e.RoutePattern.RawText?.StartsWith("/api/cvs") == true);
    }
}
