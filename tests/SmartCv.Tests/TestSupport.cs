using System.Text;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SmartCv.Api.Background;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Storage;

namespace SmartCv.Tests;

/// <summary>Dữ liệu và môi trường dùng chung cho các test tải CV (không cần SQL Server, không gọi Gemini).</summary>
internal static class TestSupport
{
    // A44 changed /api/health from unconditional liveness to dependency readiness.
    // Non-health regression tests assert the full public contract without depending on installed OCR files.
    internal static async Task AssertHealthResponseAsync(HttpResponseMessage response)
    {
        var report = await response.Content.ReadFromJsonAsync<SmartCv.Core.Ops.HealthReportDto>();
        Assert.NotNull(report);
        Assert.Equal(new[] { "database", "uploads", "ocr" }, report.Checks.Select(c => c.Name));
        var ready = report.Checks.All(c => c.Ok);
        Assert.Equal(ready ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ready ? "ok" : "error", report.Status);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
    /// <summary>A28: fixture nghiệp vụ cho các filter privacy A39/A43 vốn đòi HR khi auth được cấu hình.</summary>
    internal static async Task<HttpClient> ClientWithSyntheticHrAsync(WebApplicationFactory<Program> factory, string role = "HR")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var user = new SmartCv.Core.Entities.User
        {
            Email = Guid.NewGuid().ToString("N") + "@example.invalid", FullName = "Synthetic regression " + role,
            Role = role, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<SmartCv.Core.Entities.User>()
            .HashPassword(user, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        if (db.Database.IsSqlServer())
        {
            RegressionIdentity.ValidateTarget(db);
            // Tạo identity của fixture ngoài các interceptor lỗi/cancellation đang kiểm tra nghiệp vụ CV.
            await using var identityDb = new SmartCvDbContext(new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(db.Database.GetConnectionString()).Options);
            identityDb.Add(user); identityDb.Add(new SmartCv.Core.Entities.WorkspaceMember { WorkspaceId = 1, User = user, Role = "Member", JoinedAt = DateTime.UtcNow });
            await identityDb.SaveChangesAsync();
        }
        else
        {
            await db.EnsureInternalWorkspaceAsync();
            db.Add(user); db.Add(new SmartCv.Core.Entities.WorkspaceMember { WorkspaceId = 1, User = user, Role = "Member", JoinedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        // Clock của các fixture job/privacy có thể là ngày lịch sử; JWT luôn cần thời gian hiện tại.
        // A52: fixture nghiệp vụ là thành viên workspace nội bộ (WorkspaceId = 1) nên token mang claim smartcv_ws.
        var token = new SmartCv.Api.Auth.JwtTokenService(scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmartCv.Core.Options.JwtOptions>>(), TimeProvider.System).Create(user, 1).Token;
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    /// <summary>Nội dung PDF giả có chữ ký "%PDF-" đúng; seed khác nhau thì nội dung (và hash) khác nhau.</summary>
    public static byte[] Pdf(string seed, int size = 0)
    {
        var text = Encoding.ASCII.GetBytes($"%PDF-1.4\n% {seed}\n%%EOF\n");
        return size <= text.Length ? text : [.. text, .. new byte[size - text.Length]];
    }

    /// <summary>Ảnh PNG giả: 8 byte chữ ký PNG + seed.</summary>
    public static byte[] Png(string seed) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Encoding.ASCII.GetBytes(seed)];

    /// <summary>Ảnh JPG giả: 3 byte chữ ký JPEG + seed.</summary>
    public static byte[] Jpg(string seed) => [0xFF, 0xD8, 0xFF, 0xE0, .. Encoding.ASCII.GetBytes(seed)];

    /// <summary>SmartCvDbContext chạy trong bộ nhớ (EF Core InMemory), mỗi tên một database riêng.</summary>
    public static SmartCvDbContext InMemoryDb(string name) =>
        new(new DbContextOptionsBuilder<SmartCvDbContext>().UseInMemoryDatabase(name).Options);

    /// <summary>
    /// API chạy trong bộ nhớ: cấu hình giả, SmartCvDbContext thay bằng InMemory (tên <paramref name="dbName"/>).
    /// </summary>
    /// A7: mặc định TẮT tiến trình nền đọc chữ để kết quả test không phụ thuộc thời điểm worker chạy
    /// (CV vừa tải lên luôn là Pending). Test cần đọc chữ thật thì truyền <paramref name="extractionWorker"/> = true.
    public static WebApplicationFactory<Program> ApiFactory(
        string environment = "Development", Dictionary<string, string?>? settings = null, string? dbName = null,
        bool extractionWorker = false, bool authenticated = true)
    {
        var database = dbName ?? Guid.NewGuid().ToString("N");
        return new StartupGatedApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            // Đọc sớm trong AddInfrastructure nên phải dùng UseSetting. Giá trị giả, không dùng thật.
            builder.UseSetting("ConnectionStrings:SmartCvDb", "Server=(local);Database=SmartCv_Test_NotUsed;Trusted_Connection=True");
            builder.UseSetting("Gemini:ApiKey", "test-not-a-real-key");
            // A44 enables the real worker in Production. Test hosts remain isolated unless a test opts in.
            if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["Scoring:WorkerEnabled"] = "false", ["CvUpload:BaseDirectory"] = TempRoot() }));
            if (settings is not null)
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            }

            builder.ConfigureTestServices(services =>
            {
                if (authenticated)
                {
                    services.AddSingleton<RegressionIdentity>();
                    services.RemoveAll<Microsoft.AspNetCore.Authentication.IAuthenticationHandlerProvider>();
                    services.AddScoped<Microsoft.AspNetCore.Authentication.IAuthenticationHandlerProvider, RegressionAuthenticationHandlerProvider>();
                }
                services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                services.AddDbContext<SmartCvDbContext>(o => o.UseInMemoryDatabase(database));

                // A6: lưu file vào thư mục tạm của test, không ghi vào src/SmartCv.Api/Uploads
                services.RemoveAll<ICvFileStorage>();
                services.AddSingleton<ICvFileStorage>(Storage(TempRoot()));

                if (!extractionWorker)
                {
                    var worker = services.Where(d => d.ImplementationType == typeof(CvExtractionWorker)).ToList();
                    foreach (var d in worker) services.Remove(d);
                }
            });
        });
    }

    /// <summary>Thư mục tạm riêng cho một test (trong %TEMP%), dùng làm thư mục gốc lưu file.</summary>
    public static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smartcv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static LocalCvFileStorage Storage(string root) => new(root, "Uploads/CV");

    /// <summary>Đường dẫn tới file trong thư mục test-files ở gốc repo.</summary>
    public static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartCv.slnx")) && !Directory.Exists(Path.Combine(dir.FullName, "test-files")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Không tìm thấy gốc repo"), relative);
    }

    /// <summary>Thư mục tessdata của Api (tải bằng scripts/tai-tessdata.ps1, không có trong Git).</summary>
    public static string TessdataPath => RepoFile(Path.Combine("src", "SmartCv.Api", "tessdata"));

    public static bool HasTessdata =>
        File.Exists(Path.Combine(TessdataPath, "eng.traineddata")) && File.Exists(Path.Combine(TessdataPath, "vie.traineddata"));
}

/// <summary>
/// WebApplicationFactory gốc cho mọi test API (A10 test lifecycle).
/// Sửa race của DeferredHost (minimal hosting): <c>DeferredHost.StartAsync</c> đọc <c>host.Services</c> trên luồng test,
/// trong khi luồng chạy Program (<c>app.Run()</c>) có thể đã khởi động lỗi (ValidateOnStart) và dispose host trước đó.
/// Khi máy bận (test chạy song song), luồng test bị lập lịch chậm nên nhận ObjectDisposedException('IServiceProvider')
/// thay cho lỗi khởi động thật (ví dụ OptionsValidationException của Weights_not_summing_to_one_stop_startup).
/// Ở đây Program bị giữ tại <see cref="IHostLifetime.WaitForStartAsync"/> (chạy TRƯỚC bước kiểm tra options và hosted service)
/// cho tới khi luồng test đã đăng ký chờ xong, rồi mới mở cổng. Không đổi cấu hình hay kiểm tra options của ứng dụng;
/// khởi động thành công vẫn diễn ra như cũ. Không tắt chạy song song, không chờ theo thời gian.
/// </summary>
internal class StartupGatedApiFactory : WebApplicationFactory<Program>
{
    internal const string RegressionActor = "Synthetic regression Admin";
    // Khóa test ngẫu nhiên chỉ trong RAM của test host; không dùng User Secrets thật.
    internal static readonly string JwtTestKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = JwtTestKey,
            ["Auth:SeedAdmin:Email"] = null, ["Auth:SeedAdmin:FullName"] = null, ["Auth:SeedAdmin:Password"] = null
        }));
        // A52: code test ngoài request (gieo/đọc dữ liệu qua factory.Services) làm việc trong workspace nội bộ; request thật vẫn fail-closed.
        builder.ConfigureTestServices(services => { services.RemoveAll<SmartCv.Core.Workspaces.IWorkspaceContext>(); services.AddScoped<SmartCv.Core.Workspaces.IWorkspaceContext, TestHostWorkspaceContext>(); });
        base.ConfigureWebHost(builder);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.ConfigureServices(services => services.AddSingleton<IHostLifetime>(new GateLifetime(gate.Task)));

        var host = builder.Build();
        BeforeHostStart();
        var started = host.StartAsync();   // phần đồng bộ: DeferredHost đọc host.Services khi host chắc chắn chưa bị dispose
        gate.SetResult();                  // cho Program chạy tiếp Host.StartAsync (kiểm tra options, hosted service)
        started.GetAwaiter().GetResult();  // lỗi khởi động thật (vd. OptionsValidationException) được ném ra ở đây
        return host;
    }

    /// <summary>
    /// Điểm móc CHỈ cho test vòng đời (TestHostLifecycleTests): mô phỏng luồng test bị lập lịch chậm giữa Build và Start
    /// để tái hiện race. Mặc định không làm gì.
    /// </summary>
    protected virtual void BeforeHostStart() { }

    private sealed class GateLifetime(Task gate) : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => gate.WaitAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>Test cần OCR thật: tự bỏ qua (Skipped, không tính là PASS) khi máy chưa tải tessdata.</summary>
public sealed class OcrFactAttribute : FactAttribute
{
    public OcrFactAttribute()
    {
        if (!TestSupport.HasTessdata) Skip = "Chưa có tessdata (chạy scripts/tai-tessdata.ps1)";
    }
}
