using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Gemini;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Storage;
using SmartCv.Infrastructure.Uploads;

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

        // A4: tải CV. Đọc mục "CvUpload" trong appsettings.json.
        // Scoped vì A6 sẽ dùng SmartCvDbContext (cũng Scoped) trong service này.
        services.Configure<CvUploadOptions>(configuration.GetSection(CvUploadOptions.SectionName));
        services.AddScoped<ICvUploadService, CvUploadService>();

        // A6: lưu file CV vào {thư mục Api}/Uploads/CV (CvUpload:StorageRoot), ngoài wwwroot.
        // Singleton: không giữ trạng thái thay đổi, dùng chung cho cả ứng dụng.
        services.AddSingleton<ICvFileStorage>(sp => new LocalCvFileStorage(
            sp.GetRequiredService<IHostEnvironment>().ContentRootPath,
            sp.GetRequiredService<IOptions<CvUploadOptions>>().Value.StorageRoot,
            sp.GetRequiredService<ILogger<LocalCvFileStorage>>()));

        return services;
    }
}
