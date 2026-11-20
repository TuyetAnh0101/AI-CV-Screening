extern alias web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure;
using WebProduction = web::SmartCv.Web.Services.WebProductionSetup;

namespace SmartCv.Tests.Ops;

public class A44ProductionTests
{
    [Fact]
    public void Development_without_custom_keys_preserves_the_existing_application_discriminator()
    {
        using var baseline = new ServiceCollection().AddLogging().AddDataProtection().Services.BuildServiceProvider();
        using var configured = new ServiceCollection().AddLogging().AddSmartCvWebProductionForTest(A44LoggingTests.Config(),
            new A44LoggingTests.Host("Development", TestSupport.TempRoot())).BuildServiceProvider();
        Assert.Equal(baseline.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator,
            configured.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
    }

    [Fact]
    public void Web_keys_survive_restart_and_a_new_deployment_directory_with_the_same_application_identity()
    {
        var keys = TestSupport.TempRoot(); string encrypted;
        var config = A44LoggingTests.Config(("ApiBaseUrl", "https://api.example.invalid/"), ("DataProtection:KeysPath", keys));
        using (var services = new ServiceCollection().AddLogging().AddSmartCvWebProductionForTest(config, new A44LoggingTests.Host("Production", TestSupport.TempRoot())).BuildServiceProvider())
            encrypted = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("A44.synthetic-session").Protect("synthetic-session-data");
        Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml"));
        using var restarted = new ServiceCollection().AddLogging().AddSmartCvWebProductionForTest(config,
            new A44LoggingTests.Host("Production", TestSupport.TempRoot())).BuildServiceProvider();
        Assert.Equal("synthetic-session-data", restarted.GetRequiredService<IDataProtectionProvider>().CreateProtector("A44.synthetic-session").Unprotect(encrypted));
        Assert.Empty(Directory.GetFiles(keys, ".key-probe-*"));
    }

    [Theory]
    [InlineData("PromptApi:UseFake", "true")]
    [InlineData("ApiBaseUrl", "http://api.example.invalid/")]
    [InlineData("ApiBaseUrl", "https://user:private@api.example.invalid/")]
    [InlineData("DataProtection:KeysPath", null)]
    [InlineData("DataProtection:KeysPath", "relative-keys")]
    public void Invalid_Production_web_configuration_fails_without_changing_identity_or_credentials(string key, string? value)
    {
        var values = new Dictionary<string, string?> { ["ApiBaseUrl"] = "https://api.example.invalid/", ["DataProtection:KeysPath"] = TestSupport.TempRoot() };
        values[key] = value;
        var config = A44LoggingTests.Config(values.Select(v => (v.Key, v.Value)).ToArray());
        Assert.Throws<InvalidOperationException>(() => WebProduction.AddSmartCvWebProduction(new ServiceCollection(), config, new A44LoggingTests.Host("Production", TestSupport.TempRoot())));
    }

    [Fact]
    public void Production_keys_must_not_be_inside_the_deployment_directory()
    {
        var root = TestSupport.TempRoot();
        var config = A44LoggingTests.Config(("ApiBaseUrl", "https://api.example.invalid/"), ("DataProtection:KeysPath", Path.Combine(root, "keys")));
        Assert.Throws<InvalidOperationException>(() => WebProduction.AddSmartCvWebProduction(new ServiceCollection(), config, new A44LoggingTests.Host("Production", root)));
    }

    [Fact]
    public void Production_CV_data_must_not_be_inside_the_deployment_directory()
    {
        var root = TestSupport.TempRoot();
        var config = A44LoggingTests.Config(("ConnectionStrings:SmartCvDb", "SyntheticConnection"), ("Jwt:Key", "SyntheticKey"),
            ("Gemini:ApiKey", "SyntheticAiKey"), ("CvUpload:BaseDirectory", Path.Combine(root, "data")));
        Assert.Throws<InvalidOperationException>(() => SmartCv.Api.Ops.ProductionSetup.ValidateRequiredSettings(config, new A44LoggingTests.Host("Production", root)));
    }

    [Fact]
    public async Task Configured_data_directory_preserves_relative_database_paths_across_deployments_and_probe_leaves_CVs_untouched()
    {
        var data = TestSupport.TempRoot(); var firstDeployment = TestSupport.TempRoot(); var secondDeployment = TestSupport.TempRoot();
        var config = A44LoggingTests.Config(("ConnectionStrings:SmartCvDb", "Server=never-connect.invalid;Database=SmartCv_A44_ModelOnly;Integrated Security=true"),
            ("Gemini:ApiKey", "synthetic-unused-key"), ("CvUpload:BaseDirectory", data));
        ServiceProvider StorageHost(string root) => new ServiceCollection().AddLogging().AddSingleton<IHostEnvironment>(new A44LoggingTests.Host("Production", root))
            .AddInfrastructure(config).BuildServiceProvider();
        var id = Guid.NewGuid(); string path;
        using (var first = StorageHost(firstDeployment))
        {
            var storage = first.GetRequiredService<ICvFileStorage>();
            path = await storage.SaveAsync(id, ".pdf", new MemoryStream([1, 2, 3]), new DateTime(2026, 11, 20));
            Assert.Equal($"Uploads/CV/2026/11/{id}.pdf", path);
            await ((ICvStorageProbe)storage).CheckWritableAsync();
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(data, path)));
            Assert.Empty(Directory.GetFiles(data, ".health-*", SearchOption.AllDirectories));
        }
        using var second = StorageHost(secondDeployment);
        using var stream = second.GetRequiredService<ICvFileStorage>().OpenRead(path);
        Assert.Equal(3, stream.Length); Assert.Empty(Directory.GetFiles(firstDeployment, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(secondDeployment, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Relative_data_directory_is_rejected_before_any_CV_write()
    {
        var config = A44LoggingTests.Config(("ConnectionStrings:SmartCvDb", "Server=never-connect.invalid;Database=SmartCv_A44_ModelOnly;Integrated Security=true"),
            ("Gemini:ApiKey", "synthetic-unused-key"), ("CvUpload:BaseDirectory", "../private"));
        using var services = new ServiceCollection().AddLogging().AddSingleton<IHostEnvironment>(new A44LoggingTests.Host("Development", TestSupport.TempRoot()))
            .AddInfrastructure(config).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<ICvFileStorage>());
    }
}

internal static class WebProductionTestExtensions
{
    internal static IServiceCollection AddSmartCvWebProductionForTest(this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration config, IHostEnvironment env) => WebProduction.AddSmartCvWebProduction(services, config, env);
}
