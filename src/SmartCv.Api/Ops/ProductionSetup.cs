namespace SmartCv.Api.Ops;

public static class ProductionSetup
{
    /// <summary>Read/validate only. Never writes settings, rotates JWT keys or changes the existing admin bootstrap.</summary>
    public static void ValidateRequiredSettings(IConfiguration config, IHostEnvironment env)
    {
        if (env.IsDevelopment()) return;
        var required = new[] { "ConnectionStrings:SmartCvDb", "Jwt:Key", "Gemini:ApiKey", "CvUpload:BaseDirectory" };
        var missing = required.Where(key => string.IsNullOrWhiteSpace(config[key])).Select(key => key.Replace(":", "__")).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("Thiếu cấu hình Production bắt buộc: " + string.Join(", ", missing) + ".");
        var data = config["CvUpload:BaseDirectory"]!;
        if (!Path.IsPathFullyQualified(data)) throw new InvalidOperationException("CvUpload:BaseDirectory phải là đường dẫn tuyệt đối ngoài thư mục triển khai API.");
        var full = Path.GetFullPath(data);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(env.ContentRootPath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("CvUpload:BaseDirectory phải nằm ngoài thư mục triển khai API để bảo toàn CV khi cập nhật.");
    }
}
