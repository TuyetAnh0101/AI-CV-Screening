namespace SmartCv.Web.Services;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Đăng ký tất cả service gọi API. Program.cs chỉ cần một dòng:
    /// builder.Services.AddSmartCvApiClients(builder.Configuration);
    /// Thêm service mới (B7, B12...) thì thêm 1 dòng Add(...) vào đây.
    /// </summary>
    public static IServiceCollection AddSmartCvApiClients(this IServiceCollection services, IConfiguration configuration)
    {
        var baseUrl = configuration["ApiBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "Thiếu cấu hình \"ApiBaseUrl\" trong appsettings.json của SmartCv.Web (ví dụ: https://localhost:7001/).");
        }

        // Địa chỉ gốc phải kết thúc bằng "/" thì "api/jobs" mới ghép đúng thành https://.../api/jobs
        var baseUri = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");

        void Add<TClient>(TimeSpan timeout) where TClient : class =>
            services.AddHttpClient<TClient>(client =>
            {
                client.BaseAddress = baseUri;
                client.Timeout = timeout;
            });

        Add<HealthApiClient>(TimeSpan.FromSeconds(5));
        Add<JobApiClient>(TimeSpan.FromSeconds(30));
        Add<ApplicationApiClient>(TimeSpan.FromSeconds(30));
        Add<RunApiClient>(TimeSpan.FromSeconds(30));   // B6: tạo lượt phân tích, hỏi tiến độ
        Add<EvaluationApiClient>(TimeSpan.FromSeconds(30)); // B9: chi tiết đánh giá
        Add<CvApiClient>(TimeSpan.FromMinutes(5));   // tải nhiều file cần lâu hơn

        // B3: giới hạn tải CV, đọc mục "CvUpload" trong appsettings.json của Web.
        // PHẢI KHỚP với mục "CvUpload" của API, nếu không giao diện cho qua mà API lại từ chối.
        services.Configure<SmartCv.Core.Options.CvUploadOptions>(
            configuration.GetSection(SmartCv.Core.Options.CvUploadOptions.SectionName));

        return services;
    }
}
