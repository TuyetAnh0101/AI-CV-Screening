extern alias web;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

public sealed class B55ExternalCallbackTests
{
    [Theory]
    [InlineData("https://evil.example", "/dashboard")]
    [InlineData("//evil.example", "/dashboard")]
    [InlineData("/\\evil.example", "/dashboard")]
    [InlineData("/%2f%2fevil.example", "/dashboard")]
    [InlineData("/%5cevil.example", "/dashboard")]
    [InlineData("/results?job=12", "/results?job=12")]
    [InlineData("/dashboard", "/dashboard")]
    public void External_callback_only_navigates_to_relative_safe_path(string proposed, string expected)
        => Assert.Equal(expected, ExternalAuthUiRules.SafeDestination(proposed));

    [Fact]
    public void Login_url_uses_api_origin_and_never_web_origin()
    {
        var url = ExternalAuthUiRules.LoginStartUrl("https://localhost:7269/", "google", "/dashboard");
        Assert.StartsWith("https://localhost:7269/api/auth/external/google/start?intent=login", url);
        Assert.Contains("returnUrl=%2Fdashboard", url);
    }

    [Fact]
    public void Link_ticket_url_prepends_api_origin_and_rejects_foreign_host()
    {
        var url = ExternalAuthUiRules.LinkStartUrl("https://localhost:7269", "google",
            "/api/auth/external/google/start?intent=link&ticket=" + new string('a', 43));
        Assert.StartsWith("https://localhost:7269/api/auth/external/google/start", url);
        Assert.Throws<InvalidOperationException>(() => ExternalAuthUiRules.LinkStartUrl("https://localhost:7269", "google", "https://evil.example"));
    }

    [Fact]
    public void Code_must_be_43_base64url_characters()
    {
        Assert.True(ExternalAuthUiRules.IsValidCode(new string('A', 43)));
        Assert.False(ExternalAuthUiRules.IsValidCode("broken"));
        Assert.False(ExternalAuthUiRules.IsValidCode(new string('!', 43)));
    }

    [Theory]
    [InlineData("link_required", "liên kết")]
    [InlineData("registration_disabled", "đăng ký")]
    [InlineData("email_unverified", "xác minh")]
    [InlineData("account_locked", "khóa")]
    [InlineData("admin_not_allowed", "Admin")]
    [InlineData("already_linked", "liên kết")]
    public void External_errors_have_vietnamese_messages(string code, string term)
        => Assert.Contains(term, AuthText.ExternalLoginError(code), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Exchange_sends_code_only_in_json_body_with_no_bearer_or_query()
    {
        var raw = new string('Q', 43);
        using var handler = new Handler(async req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("/api/auth/external/exchange", req.RequestUri!.AbsolutePath);
            Assert.Null(req.Headers.Authorization);
            Assert.False(req.RequestUri.Query.Contains(raw));
            Assert.Contains(raw, await req.Content!.ReadAsStringAsync());
            var json = "{\"token\":\"jwt-placeholder\",\"expiresAt\":\"2026-12-01T00:00:00Z\",\"user\":{\"userId\":2,\"fullName\":\"Test\",\"email\":\"test@example.com\",\"role\":\"HR\",\"mustChangePassword\":false},\"returnUrl\":\"/dashboard\",\"isNewWorkspace\":false}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.invalid/") };
        var api = new AuthApiClient(http, NullLogger<AuthApiClient>.Instance);
        var result = await api.ExchangeExternalCodeAsync(raw);
        Assert.True(result.IsSuccess);
        Assert.Equal("/dashboard", result.Data!.ReturnUrl);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
