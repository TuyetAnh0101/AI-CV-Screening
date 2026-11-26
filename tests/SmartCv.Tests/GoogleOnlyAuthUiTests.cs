extern alias web;

using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components.Account;
using web::SmartCv.Web.Components.Shared;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

public sealed class GoogleOnlyAuthUiTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("github")]
    [InlineData("linkedin")]
    [InlineData("unknown")]
    [InlineData("Google")]
    [InlineData(" google ")]
    public void Unsupported_provider_cannot_build_a_login_or_link_URL(string? provider)
    {
        Assert.False(ExternalProviders.IsSupported(provider));
        Assert.False(ExternalAuthUiRules.SupportedProvider(provider));
        Assert.Throws<ArgumentException>(() => ExternalAuthUiRules.LoginStartUrl("https://api.test", provider!, "/account"));
        Assert.Throws<InvalidOperationException>(() => ExternalAuthUiRules.LinkStartUrl("https://api.test", provider!,
            $"/api/auth/external/{provider}/start?intent=link&ticket={new string('A', 43)}"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sign_in_buttons_ignore_enabled_unsupported_providers(bool googleEnabled)
    {
        var html = await RenderAsync<ExternalLoginButtons>(googleEnabled: googleEnabled);
        Assert.DoesNotContain("GitHub", html);
        Assert.DoesNotContain("LinkedIn", html);
        if (googleEnabled)
        {
            Assert.Contains("Tiếp tục với Google", html);
            Assert.Contains("type=\"button\"", html);
        }
        else
        {
            Assert.DoesNotContain("<button", html);
            Assert.DoesNotContain("b55-social-divider", html);
        }
    }

    [Theory]
    [InlineData("github", false)]
    [InlineData("github", true)]
    [InlineData("linkedin", false)]
    [InlineData("unknown", false)]
    [InlineData("google", false)]
    [InlineData("Google", true)]
    [InlineData("google&linked=github", true)]
    public async Task Account_query_cannot_claim_a_link_that_the_profile_does_not_confirm(string query, bool googleLinked)
    {
        var html = await RenderAsync<AccountPage>("https://app.test/account?linked=" + query, googleLinked: googleLinked);
        Assert.DoesNotContain("b55-banner-ok", html);
        Assert.DoesNotContain("Đã liên kết tài khoản GitHub", html);
        if (!googleLinked) Assert.Contains("Chưa kết nối", html);
    }

    [Fact]
    public async Task Account_confirms_Google_link_from_authenticated_profile()
    {
        var html = await RenderAsync<AccountPage>("https://app.test/account?linked=google", googleLinked: true);
        Assert.Contains("b55-banner-ok", html);
        Assert.Contains("Tài khoản Google đã được liên kết.", html);
        Assert.Contains("b55-unlink-google", html);
        Assert.DoesNotContain("GitHub", html);
    }

    [Fact]
    public async Task Failed_profile_read_keeps_its_error_instead_of_a_query_success()
    {
        var html = await RenderAsync<AccountPage>("https://app.test/account?linked=google", googleLinked: true,
            profileStatus: HttpStatusCode.ServiceUnavailable);
        Assert.Contains("Không lấy được hồ sơ", html);
        Assert.Contains("b55-banner-error", html);
        Assert.DoesNotContain("b55-banner-ok", html);
    }

    [Fact]
    public async Task Callback_error_takes_precedence_over_a_link_query()
    {
        var html = await RenderAsync<AccountPage>("https://app.test/account?linked=google&external_error=already_linked",
            googleLinked: true);
        Assert.Contains("b55-banner-error", html);
        Assert.DoesNotContain("b55-banner-ok", html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Account_methods_offer_only_enabled_Google_and_preserve_password_access(bool googleEnabled)
    {
        var profile = Profile(googleLinked: false);
        var html = await RenderAsync<LoginMethodsCard>(googleEnabled: googleEnabled,
            parameters: new() { [nameof(LoginMethodsCard.Profile)] = profile });
        Assert.Contains("Email và mật khẩu", html);
        Assert.DoesNotContain("GitHub", html);
        Assert.DoesNotContain("LinkedIn", html);
        if (googleEnabled) Assert.Contains(">Liên kết</button>", html);
        else
        {
            Assert.Contains("chưa có phương thức đăng nhập ngoài được bật", html);
            Assert.DoesNotContain(">Liên kết</button>", html);
        }
    }

    [Theory]
    [InlineData("google", "Google")]
    [InlineData("github", "GitHub")]
    [InlineData("linkedin", "LinkedIn")]
    public void Historical_signup_labels_do_not_enable_authentication(string source, string expected)
    {
        Assert.Equal(expected, UserText.SignupSource(source));
        Assert.Equal(source == ExternalProviders.Google, ExternalAuthUiRules.SupportedProvider(source));
    }

    private static ProfileDto Profile(bool googleLinked)
    {
        var now = DateTime.UtcNow;
        var logins = new List<LinkedLoginDto>
        {
            new("github", "history@example.invalid", now, null),
            new("linkedin", "history@example.invalid", now, null)
        };
        if (googleLinked) logins.Add(new("google", "google@example.invalid", now, null));
        return new(7, "Tài khoản tổng hợp", "test@example.invalid", true, true, "HR", "Local", now, null, null, logins);
    }

    private static async Task<string> RenderAsync<T>(string uri = "https://app.test/login", bool googleEnabled = true,
        bool googleLinked = false, HttpStatusCode profileStatus = HttpStatusCode.OK,
        Dictionary<string, object?>? parameters = null) where T : IComponent
    {
        var profile = Profile(googleLinked);
        using var handler = new B27CaptureHandler
        {
            Respond = request => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/auth/registration-settings" => B27CaptureHandler.Json(HttpStatusCode.OK,
                    new RegistrationSettingsResponse(true, true,
                        [new("google", googleEnabled), new("github", true), new("linkedin", true)],
                        new(8, 128, true, true), "2026-11")),
                "/api/auth/me/profile" when profileStatus == HttpStatusCode.OK => B27CaptureHandler.Json(HttpStatusCode.OK, profile),
                "/api/auth/me/profile" => B27CaptureHandler.Problem(profileStatus, "account_unavailable"),
                "/api/auth/me/logins" => B27CaptureHandler.Json(HttpStatusCode.OK, profile.Logins),
                "/api/workspace/" => B27CaptureHandler.Problem(HttpStatusCode.Forbidden, "forbidden"),
                _ => throw new InvalidOperationException("Unexpected UI test route: " + request.RequestUri.AbsolutePath)
            })
        };
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.test/") };
        var session = B27.Session();
        await session.SignInAsync(B27.Login("synthetic-ui-session", "HR"));
        var collection = new ServiceCollection().AddLogging();
        collection.AddSingleton(session);
        collection.AddSingleton<NavigationManager>(new Nav(uri));
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ApiBaseUrl"] = "https://api.test/" }).Build());
        collection.AddSingleton<IRegistrationApi>(new AuthApiClient(http, NullLogger<AuthApiClient>.Instance));
        collection.AddSingleton(new AccountApiClient(http, NullLogger<AccountApiClient>.Instance));
        collection.AddSingleton(new WorkspaceApiClient(http, NullLogger<WorkspaceApiClient>.Instance));
        collection.AddSupplyValueFromQueryProvider();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode(
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters ?? []))).ToHtmlString()));
    }

    private sealed class Nav : NavigationManager
    {
        internal Nav(string uri) => Initialize("https://app.test/", uri);
    }
}
