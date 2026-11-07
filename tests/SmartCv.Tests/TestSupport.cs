using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Storage;

namespace SmartCv.Tests;

/// <summary>Dữ liệu và môi trường dùng chung cho các test tải CV (không cần SQL Server, không gọi Gemini).</summary>
internal static class TestSupport
{
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
    public static WebApplicationFactory<Program> ApiFactory(
        string environment = "Development", Dictionary<string, string?>? settings = null, string? dbName = null)
    {
        var database = dbName ?? Guid.NewGuid().ToString("N");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            // Đọc sớm trong AddInfrastructure nên phải dùng UseSetting. Giá trị giả, không dùng thật.
            builder.UseSetting("ConnectionStrings:SmartCvDb", "Server=(local);Database=SmartCv_Test_NotUsed;Trusted_Connection=True");
            builder.UseSetting("Gemini:ApiKey", "test-not-a-real-key");
            if (settings is not null)
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                services.AddDbContext<SmartCvDbContext>(o => o.UseInMemoryDatabase(database));

                // A6: lưu file vào thư mục tạm của test, không ghi vào src/SmartCv.Api/Uploads
                services.RemoveAll<ICvFileStorage>();
                services.AddSingleton<ICvFileStorage>(Storage(TempRoot()));
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
}
