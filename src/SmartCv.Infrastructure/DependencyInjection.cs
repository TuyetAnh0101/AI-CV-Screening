using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Infrastructure.Gemini;
using SmartCv.Infrastructure.Persistence;

namespace SmartCv.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // SQL Server
        var connectionString = configuration.GetConnectionString("SmartCvDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Chưa cấu hình ConnectionStrings:SmartCvDb. Xem README, mục User Secrets.");

        services.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(connectionString));

        // Gemini: kiểm tra cấu hình ngay khi khởi động, thiếu key thì báo lỗi rõ ràng
        services.AddOptions<GeminiOptions>()
            .Bind(configuration.GetSection(GeminiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient("Gemini", client =>
        {
            client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        return services;
    }
}