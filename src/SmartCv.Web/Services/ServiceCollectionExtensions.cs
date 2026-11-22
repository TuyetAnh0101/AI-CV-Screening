using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // B52: public settings API. Không dùng ApiClientBase.UseSession hoặc Bearer.
        services.AddHttpClient("SCAppPublicSettings", client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(6);
        }).RedactLoggedHeaders(_ => true);
        services.AddScoped<PublicSettingsClient>(sp =>
            new PublicSettingsClient(sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient("SCAppPublicSettings")));

        // B27: phiên đăng nhập của từng tab (Scoped = một circuit Blazor). Không singleton/static giữ token.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ITokenStore, ProtectedSessionTokenStore>();
        services.AddScoped<AuthSession>();
        services.AddScoped<RegistrationFlowState>(); // B53: chi luu email trong circuit
        services.AddScoped<IAuthApi>(sp => sp.GetRequiredService<AuthApiClient>());
        services.AddScoped<IRegistrationApi>(sp => sp.GetRequiredService<AuthApiClient>());
        services.AddScoped<IAuthRecoveryApi>(sp => sp.GetRequiredService<AuthApiClient>()); // B54 public recovery
        // B28: vai trò cho AuthorizeRouteView / quyền thao tác, đọc từ AuthSession ở trên (không phải phiên thứ hai).
        services.AddSmartCvWebAuth();

        // authenticated = true: client được gắn AuthSession của scope đang yêu cầu nó (scope của circuit khi một component
        // inject client). Token được đặt lên từng HttpRequestMessage lúc gửi (ApiClientBase), không đặt lên HttpClient dùng chung.
        void Add<TClient>(TimeSpan timeout, bool authenticated = true) where TClient : ApiClientBase
        {
            var clientBuilder = services.AddHttpClient(typeof(TClient).Name, client =>
            {
                client.BaseAddress = baseUri;
                client.Timeout = timeout;
            });
            // Log của HttpClientFactory (mức Trace) không bao giờ in giá trị header, kể cả Authorization.
            clientBuilder.RedactLoggedHeaders(_ => true);
            // HttpClient's default logger includes the entire URI, including private search queries.
            // B29: tìm tài khoản theo email cũng là dữ liệu riêng tư.
            if (typeof(TClient) == typeof(CvApiClient) || typeof(TClient) == typeof(CandidateApiClient) || typeof(TClient) == typeof(UserApiClient) || typeof(TClient) == typeof(AuditApiClient) || typeof(TClient) == typeof(ExportApiClient))
                clientBuilder.RemoveAllLoggers();
            if (authenticated)
            {
                clientBuilder.AddTypedClient<TClient>((http, scope) =>
                {
                    var client = ActivatorUtilities.CreateInstance<TClient>(scope, http);
                    client.UseSession(scope.GetRequiredService<AuthSession>());
                    return client;
                });
            }
            else
            {
                clientBuilder.AddTypedClient<TClient>();
            }
        }

        // Công khai: kiểm tra API còn chạy và đăng nhập không cần (và không được gửi) Bearer.
        Add<HealthApiClient>(TimeSpan.FromSeconds(5), authenticated: false);
        Add<AuthApiClient>(TimeSpan.FromSeconds(20), authenticated: false); // B27: A28 đăng nhập, /api/auth/me với token chỉ định

        // Mọi client nghiệp vụ dưới đây đều gửi kèm Bearer của tab đang dùng (A29 bắt buộc).
        Add<JobApiClient>(TimeSpan.FromSeconds(30));
        Add<UserApiClient>(TimeSpan.FromSeconds(20)); // B29: quản lý tài khoản (A30, chỉ Admin)
        Add<AuditApiClient>(TimeSpan.FromSeconds(20));
        Add<ExportApiClient>(TimeSpan.FromSeconds(20));
        Add<DashboardApiClient>(TimeSpan.FromSeconds(30)); // B19: A19 summary, read-only.
        Add<ApplicationApiClient>(TimeSpan.FromSeconds(30));
        Add<RunApiClient>(TimeSpan.FromSeconds(30));   // B6: tạo lượt phân tích, hỏi tiến độ
        Add<EvaluationApiClient>(TimeSpan.FromSeconds(30)); // B9: chi tiết đánh giá
        Add<PromptApiClient>(TimeSpan.FromSeconds(30)); // B12: xem và tạo phiên bản prompt
        Add<AiPreviewApiClient>(TimeSpan.FromMinutes(5)); // B39: chấm thử chờ Gemini (có thể thử lại), lâu hơn 30 giây
        Add<CvApiClient>(TimeSpan.FromMinutes(5));   // tải nhiều file cần lâu hơn
        Add<CandidateApiClient>(TimeSpan.FromSeconds(30)); // B23/B24: tìm, xem, tạo, sửa, gộp ứng viên; xuất/xóa dữ liệu (A43).

        // B3: giới hạn tải CV, đọc mục "CvUpload" trong appsettings.json của Web.
        // PHẢI KHỚP với mục "CvUpload" của API, nếu không giao diện cho qua mà API lại từ chối.
        services.Configure<SmartCv.Core.Options.CvUploadOptions>(
            configuration.GetSection(SmartCv.Core.Options.CvUploadOptions.SectionName));

        services.AddScoped<Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage.ProtectedSessionStorage>();
        services.AddScoped<HrActorState>();
        services.AddScoped<FileDownloadService>();
        services.AddScoped<Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage.ProtectedLocalStorage>();
        services.AddScoped<AnonymityState>();
        services.AddScoped<CandidateHubState>(); // B24: từ khóa tìm người + phiên bản dữ liệu dùng chung giữa hai chế độ, chỉ trong phiên
        return services;
    }
}
