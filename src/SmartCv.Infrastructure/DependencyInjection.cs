using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Gemini;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Extraction;
using SmartCv.Infrastructure.Scoring;
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

        // Gemini: kiểm tra cấu hình ngay khi khởi động, thiếu key thì báo lỗi rõ ràng (AI giả không cần key)
        services.AddOptions<GeminiOptions>()
            .Bind(configuration.GetSection(GeminiOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.UseFake || !string.IsNullOrWhiteSpace(o.ApiKey),
                "Chưa cấu hình Gemini:ApiKey. Xem README, mục User Secrets.")
            .Validate<IHostEnvironment>((o, env) => !o.UseFake || env.IsDevelopment(),
                "Gemini:UseFake chỉ được bật ở môi trường Development.")
            .ValidateOnStart();

        // A10: chấm bằng AI. Chọn bản thật / giả lúc resolve để luôn đọc cấu hình đã hoàn chỉnh.
        services.AddSingleton<GeminiRateLimiter>();   // dùng chung một "cửa" cho mọi lần gọi Gemini trong ứng dụng
        services.AddHttpClient<GeminiEvaluationService>((sp, client) =>
        {
            var gemini = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
            client.BaseAddress = new Uri(gemini.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(gemini.TimeoutSeconds);
        });
        services.AddTransient<FakeAiEvaluator>();
        services.AddTransient<IAiEvaluator>(sp => sp.GetRequiredService<IOptions<GeminiOptions>>().Value.UseFake
            ? sp.GetRequiredService<FakeAiEvaluator>()
            : sp.GetRequiredService<GeminiEvaluationService>());

        services.AddHttpClient("Gemini", client =>
        {
            client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        // A11: chấm một lượt; dùng DbContext nên Scoped, worker tạo scope mới cho mỗi lượt.
        services.AddScoped<RunScoringProcessor>();

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

        // A7: đọc chữ CV. Đọc mục "TextExtraction" trong appsettings.json.
        // TessdataPath tương đối ("tessdata") được đổi thành đường dẫn đầy đủ trong thư mục dự án Api.
        services.Configure<OcrOptions>(configuration.GetSection(OcrOptions.SectionName));
        services.AddOptions<OcrOptions>().PostConfigure<IHostEnvironment>((o, env) =>
            o.TessdataPath = Path.GetFullPath(Path.Combine(env.ContentRootPath, o.TessdataPath)));

        services.AddSingleton<ICvExtractionQueue, CvExtractionQueue>();   // hàng đợi dùng chung (upload + worker)
        services.AddSingleton<TesseractOcrEngine>();                        // tải mô hình OCR một lần
        services.AddSingleton<ICvTextExtractor, CvTextExtractor>();
        services.AddScoped<CvExtractionProcessor>();                        // dùng DbContext nên Scoped

        return services;
    }
}
