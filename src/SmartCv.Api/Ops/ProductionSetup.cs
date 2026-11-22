
namespace SmartCv.Api.Ops;

public static class ProductionSetup
{
    public static void ValidateRequiredSettings(
        IConfiguration config,
        IHostEnvironment env)
    {
        // Development không áp dụng bắt buộc cấu hình Production.
        if (env.IsDevelopment())
            return;

        // Giữ nguyên điều kiện A44.
        var required = new[]
        {
            "ConnectionStrings:SmartCvDb",
            "Jwt:Key",
            "Gemini:ApiKey",
            "CvUpload:BaseDirectory"
        };

        var missing = required
            .Where(key => string.IsNullOrWhiteSpace(config[key]))
            .Select(key => key.Replace(":", "__"))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Thiếu cấu hình Production bắt buộc: " +
                string.Join(", ", missing) + ".");
        }

        var data = config["CvUpload:BaseDirectory"]!;

        if (!Path.IsPathFullyQualified(data))
        {
            throw new InvalidOperationException(
                "CvUpload:BaseDirectory phải là đường dẫn tuyệt đối ngoài thư mục triển khai API.");
        }

        var full = Path.GetFullPath(data);

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(env.ContentRootPath));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (full.Equals(root, comparison) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException(
                "CvUpload:BaseDirectory phải nằm ngoài thư mục triển khai API để bảo toàn CV khi cập nhật.");
        }

        // A55: Data Protection phải có kho khóa bền vững.
        // Nếu mất khóa, payload email cũ không thể giải mã.
        var keysPath = config["DataProtection:KeysPath"];

        if (string.IsNullOrWhiteSpace(keysPath))
        {
            throw new InvalidOperationException(
                "Thiếu cấu hình Production bắt buộc: DataProtection__KeysPath.");
        }

        if (!Path.IsPathFullyQualified(keysPath))
        {
            throw new InvalidOperationException(
                "DataProtection:KeysPath phải là đường dẫn tuyệt đối.");
        }

        // A55: khi dùng SMTP ở Production, không được thiếu
        // bất kỳ cấu hình quan trọng nào.
        if (string.Equals(
            config["Email:Mode"],
            "Smtp",
            StringComparison.OrdinalIgnoreCase))
        {
            var emailRequired = new[]
            {
                "Email:Smtp:UserName",
                "Email:Smtp:Password",
                "Email:FromAddress",
                "App:WebBaseUrl"
            };

            var emailMissing = emailRequired
                .Where(key => string.IsNullOrWhiteSpace(config[key]))
                .Select(key => key.Replace(":", "__"))
                .ToArray();

            if (emailMissing.Length > 0)
            {
                throw new InvalidOperationException(
                    "Thiếu cấu hình SMTP Production: " +
                    string.Join(", ", emailMissing) + ".");
            }

            if (!Uri.TryCreate(
                    config["App:WebBaseUrl"],
                    UriKind.Absolute,
                    out var webUrl) ||
                webUrl.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    "App:WebBaseUrl phải là URL HTTPS Production.");
            }
        }
    }
}
