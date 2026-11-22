using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SmartCv.Api.Ops;
using SmartCv.Core.Options;

namespace SmartCv.Tests.Email;

public sealed class A55ConfigurationTests
{
    private sealed class FakeProduction : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "SmartCv.Api";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "scapp-a55-app");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void UnknownEmailModeIsInvalid() => Assert.False(new EmailOptions { Mode = "Unknown" }.IsValid());

    [Fact]
    public void ProductionSmtpWithoutPasswordFailsWithKeyName()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:SmartCvDb"] = "Server=local;Database=fake;Trusted_Connection=true",
            ["Jwt:Key"] = "TEST_ONLY_KEY",
            ["Gemini:ApiKey"] = "TEST_ONLY_KEY",
            ["CvUpload:BaseDirectory"] = Path.Combine(Path.GetTempPath(), "scapp-a55-private"),
            ["DataProtection:KeysPath"] = Path.Combine(Path.GetTempPath(), "scapp-a55-keys"),
            ["Email:Mode"] = "Smtp",
            ["Email:Smtp:UserName"] = "test@example.test",
            ["Email:FromAddress"] = "test@example.test",
            ["App:WebBaseUrl"] = "https://scapp.test"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ProductionSetup.ValidateRequiredSettings(config, new FakeProduction()));
        Assert.Contains("Email__Smtp__Password", ex.Message);
    }
}
