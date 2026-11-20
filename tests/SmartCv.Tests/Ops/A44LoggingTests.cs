using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Serilog;
using SmartCv.Api.Ops;
using SmartCv.Core.Privacy;

namespace SmartCv.Tests.Ops;

public class A44LoggingTests
{
    internal static IConfiguration Config(params (string Key, string? Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();
    internal sealed class Host(string environment, string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "SCApp.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Real_daily_file_redacts_properties_scopes_nested_data_literals_and_exceptions()
    {
        var root = TestSupport.TempRoot(); var secret = "SyntheticPrivatePassword987";
        using (var logger = LoggingSetup.CreateLogger(Config(("Logging:File:Enabled", "true"), ("Logging:File:Directory", root)), new Host("Production", root)))
        {
            logger.ForContext("AccessToken", secret).ForContext("RequestPath", "/api/cvs?search=NguyenVanAn")
                .Information("CV {CandidateName} {Email} {Phone} {FileName} {Password} {Token} {Search} {@Details}; run {RunId}",
                    "Nguyễn Văn An", "private@example.invalid", 912345678L, "CV_NguyenVanAn.pdf", secret, secret, "NguyenVanAn",
                    new { Inner = new { FullName = "Nguyễn Văn An", ApiKey = secret, Body = "CV_PRIVATE_CONTENT" },
                        Map = new Dictionary<string, string> { ["private@example.invalid"] = secret } }, 42);
            logger.Information("Literal private@example.invalid 0912 345 678 CV_NguyenVanAn.pdf Password=" + secret);
            logger.Error(new InvalidOperationException("SQL " + secret + " private@example.invalid C:\\private\\CV_NguyenVanAn.pdf"), "Failed run {RunId}", 42);
            logger.Information("Completed run {RunId}: {Count} CV", 42, 3);
            logger.Warning("SQL {SqlNumber}; mode {UserAccess}", "208", "MULTI_USER");
        }
        var path = Assert.Single(Directory.GetFiles(root, "smartcv-api-*.log"));
        Assert.Matches(@"smartcv-api-\d{8}.*\.log$", path);
        var output = File.ReadAllText(path);
        foreach (var privateValue in new[] { secret, "Nguyễn Văn An", "NguyenVanAn", "private@example.invalid", "912345678", "0912 345 678", "CV_PRIVATE_CONTENT", "C:\\private" })
            Assert.DoesNotContain(privateValue, output);
        Assert.Contains("InvalidOperationException", output); Assert.Contains("Completed run 42: 3 CV", output);
        Assert.Contains("SQL 208; mode MULTI_USER", output);
        Assert.Empty(Directory.GetFiles(root, ".log-probe-*"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    public void Invalid_log_retention_is_a_safe_configuration_failure(string days)
    {
        var root = TestSupport.TempRoot();
        Assert.Throws<InvalidOperationException>(() => LoggingSetup.CreateLogger(Config(("Logging:File:Enabled", "true"), ("Logging:File:RetainDays", days)), new Host("Production", root)));
    }

    [Fact]
    public void Unwritable_log_destination_fails_with_no_path_or_inner_exception()
    {
        var root = TestSupport.TempRoot(); var destination = Path.Combine(root, "synthetic-private-directory"); File.WriteAllText(destination, "fixture");
        var error = Assert.Throws<InvalidOperationException>(() => LoggingSetup.CreateLogger(Config(("Logging:File:Enabled", "true"), ("Logging:File:Directory", destination)), new Host("Production", root)));
        Assert.Null(error.InnerException); Assert.DoesNotContain(destination, error.Message);
    }

    [Fact]
    public void Required_Production_settings_report_names_only_and_do_not_modify_configuration()
    {
        var config = Config(); var error = Assert.Throws<InvalidOperationException>(() => ProductionSetup.ValidateRequiredSettings(config, new Host("Production", TestSupport.TempRoot())));
        foreach (var key in new[] { "ConnectionStrings__SmartCvDb", "Jwt__Key", "Gemini__ApiKey", "CvUpload__BaseDirectory" }) Assert.Contains(key, error.Message);
        ProductionSetup.ValidateRequiredSettings(config, new Host("Development", TestSupport.TempRoot()));
        var supplied = Config(("ConnectionStrings:SmartCvDb", "SyntheticConnection"), ("Jwt:Key", "SyntheticSigningValue"),
            ("Gemini:ApiKey", "SyntheticAiValue"), ("CvUpload:BaseDirectory", TestSupport.TempRoot()));
        ProductionSetup.ValidateRequiredSettings(supplied, new Host("Production", TestSupport.TempRoot()));
        Assert.Equal("SyntheticSigningValue", supplied["Jwt:Key"]);
    }

    [Fact]
    public void Free_text_redaction_reuses_existing_privacy_rules_and_preserves_operational_counts()
    {
        var safe = LogMasking.MaskText("Liên hệ private@example.invalid, 0912 345 678; Bearer PrivateToken; file CV_An.pdf");
        foreach (var secret in new[] { "private@example.invalid", "0912 345 678", "PrivateToken", "CV_An.pdf" }) Assert.DoesNotContain(secret, safe);
        Assert.Equal("Lượt #12 xong: 100 CV, 2 lỗi", LogMasking.MaskText("Lượt #12 xong: 100 CV, 2 lỗi"));
    }
}
