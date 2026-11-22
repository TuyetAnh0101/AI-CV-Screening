using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;
using SmartCv.Core.Email;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Email;

namespace SmartCv.Tests.Email;

public sealed class A55FileSenderTests
{
    private sealed class FakeHost(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "SmartCv.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task FileModeWritesEmlWithTextAndHtml()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scapp-a55-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new EmailOptions { PickupDirectory = directory });
            var sender = new FileEmailSender(options, new FakeHost(directory), new EmailMimeFactory(options));
            await sender.SendAsync(5, new EmailMessage("user@example.test", "Kiểm thử",
                "<a href='https://scapp.test/verify?token=TEST'>Mở</a>", "https://scapp.test/verify?token=TEST"));
            var files = Directory.GetFiles(directory, "*.eml");
            Assert.Single(files);
            using var stream = File.OpenRead(files[0]);
            var mime = MimeMessage.Load(stream);
            Assert.Contains("scapp.test/verify", mime.TextBody);
            Assert.Contains("scapp.test/verify", mime.HtmlBody);
            await sender.SendAsync(5, new EmailMessage("user@example.test", "Kiểm thử", "X", "X"));
            Assert.Single(Directory.GetFiles(directory, "*.eml"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
