using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SmartCv.Tests;

/// <summary>
/// API dùng cho giao diện SmartCv.Web: GET /api/health, /api/jobs, /api/jobs/{id}.
/// Không kết nối database (cấu hình giả): chỉ kiểm tra /api/health và việc đăng ký route.
/// Dữ liệu 5 job thật được kiểm tra bằng curl trên SQL Server (README mục 13.5).
/// </summary>
public class JobEndpointsTests
{
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:SmartCvDb", "Server=(local);Database=SmartCv_Test_NotUsed;Trusted_Connection=True");
            builder.UseSetting("Gemini:ApiKey", "test-not-a-real-key");
        });

    [Fact]
    public async Task Health_returns_ok_without_database()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        Assert.Equal("ok", body?.Status);
    }

    [Fact]
    public void Job_routes_are_registered_once_and_health_routes_do_not_collide()
    {
        using var factory = CreateFactory();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText?.TrimStart('/'))
            .ToList();

        Assert.Single(routes, r => r == "api/health");
        Assert.Single(routes, r => r == "api/jobs/");
        Assert.Single(routes, r => r == "api/jobs/{id:int}");
        // Các API kiểm tra kết nối của A1 vẫn còn
        Assert.Contains("api/health/database", routes);
        Assert.Contains("api/health/gemini", routes);
    }

    private sealed record HealthBody(string Status, DateTime Time);
}
