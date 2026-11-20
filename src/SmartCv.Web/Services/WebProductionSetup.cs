using Microsoft.AspNetCore.DataProtection;

namespace SmartCv.Web.Services;

public static class WebProductionSetup
{
    public static IServiceCollection AddSmartCvWebProduction(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        if (!env.IsDevelopment() && config.GetValue("PromptApi:UseFake", false))
            throw new InvalidOperationException("PromptApi:UseFake chỉ được dùng ở Development.");
        if (!env.IsDevelopment() && (!Uri.TryCreate(config["ApiBaseUrl"], UriKind.Absolute, out var api) || api.Scheme != Uri.UriSchemeHttps
            || api.UserInfo.Length > 0 || api.Query.Length > 0 || api.Fragment.Length > 0))
            throw new InvalidOperationException("ApiBaseUrl phải là địa chỉ HTTPS tuyệt đối ở Production.");
        var keys = config["DataProtection:KeysPath"];
        if (!env.IsDevelopment() && string.IsNullOrWhiteSpace(keys))
            throw new InvalidOperationException("Thiếu DataProtection__KeysPath: cần thư mục khóa cố định ngoài thư mục triển khai Web.");
        var protection = services.AddDataProtection();
        // Preserve existing local sessions; fixed identity is for deployed/persisted key rings.
        if (!env.IsDevelopment() || !string.IsNullOrWhiteSpace(keys)) protection.SetApplicationName("SmartCv.Web");
        if (!string.IsNullOrWhiteSpace(keys))
        {
            if (!Path.IsPathFullyQualified(keys)) throw new InvalidOperationException("DataProtection:KeysPath phải là đường dẫn tuyệt đối.");
            var fullPath = Path.GetFullPath(keys);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(env.ContentRootPath)) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!env.IsDevelopment() && (fullPath.StartsWith(root, comparison) || fullPath.Equals(root.TrimEnd(Path.DirectorySeparatorChar), comparison)))
                throw new InvalidOperationException("DataProtection:KeysPath phải nằm ngoài thư mục triển khai Web.");
            try
            {
                Directory.CreateDirectory(fullPath);
                using var probe = new FileStream(Path.Combine(fullPath, ".key-probe-" + Guid.NewGuid().ToString("N")),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                probe.WriteByte(1);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException("Không ghi được thư mục khóa Data Protection đã cấu hình."); }
            protection.PersistKeysToFileSystem(new DirectoryInfo(fullPath));
            if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        }
        return services;
    }
}
