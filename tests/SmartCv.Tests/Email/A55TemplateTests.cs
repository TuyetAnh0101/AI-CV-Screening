using Microsoft.Extensions.Configuration;
using SmartCv.Core.Email;
using SmartCv.Infrastructure.Email;

namespace SmartCv.Tests.Email;

public sealed class A55TemplateTests
{
    private static EmailTemplates Renderer() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string,string?> { ["App:WebBaseUrl"] = "https://scapp.test" }).Build());

    [Fact]
    public void NameAndProviderAreHtmlEscaped()
    {
        var m = Renderer().Render(EmailTemplate.RegisteredWithProvider,
            new EmailTemplateModel("sample@example.test", "<script>alert(1)</script>", "", "<b>Provider</b>"));
        Assert.DoesNotContain("<script>", m.Html);
        Assert.Contains("&lt;script&gt;", m.Html);
        Assert.DoesNotContain("<b>Provider</b>", m.Html);
    }

    [Theory]
    [InlineData(EmailTemplate.VerifyEmail)]
    [InlineData(EmailTemplate.ResetPassword)]
    public void LinkUsesConfiguredHost(EmailTemplate template)
    {
        var m = Renderer().Render(template,
            new EmailTemplateModel("sample@example.test", "A", "/verify?token=abc123&v=1"));
        Assert.Contains("https://scapp.test/verify?token=abc123", m.Text);
        Assert.Contains("&amp;v=1", m.Html);
    }

    [Fact]
    public void ExternalDomainAndSchemeRelativePathsRejected()
    {
        Assert.Throws<ArgumentException>(() => Renderer().Render(EmailTemplate.ResetPassword,
            new EmailTemplateModel("sample@example.test", "A", "//evil.test/path")));
    }

    [Fact]
    public void EveryTemplateHasBothBodies()
    {
        foreach (var template in Enum.GetValues<EmailTemplate>())
        {
            var model = new EmailTemplateModel("sample@example.test", "A", "/verify?token=abc");
            var m = Renderer().Render(template, model);
            Assert.NotEmpty(m.Subject);
            Assert.NotEmpty(m.Text);
            Assert.NotEmpty(m.Html);
        }
    }
}
