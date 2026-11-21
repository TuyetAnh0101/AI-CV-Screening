extern alias web;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using web::SmartCv.Web.Components.Layout;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Services;
using PrivacyPage = web::SmartCv.Web.Components.Pages.Privacy;
using PublicLayoutComponent = web::SmartCv.Web.Components.Layout.PublicLayout;

namespace SmartCv.Tests;

public sealed class B52LandingTests
{
    [Fact]
    public void Public_routes_are_explicit_and_use_public_layout()
    {
        foreach (var page in new[] { typeof(Landing), typeof(Terms),typeof(PrivacyPage) })
        {
            Assert.Contains(page.GetCustomAttributes(true), a => a is IAllowAnonymous);
            Assert.Equal(typeof(PublicLayoutComponent), page.GetCustomAttribute<LayoutAttribute>()?.LayoutType);
        }
        Assert.Contains(typeof(Terms).GetCustomAttributes<RouteAttribute>(), a => a.Template == "/terms");
        Assert.Contains(typeof(PrivacyPage).GetCustomAttributes<RouteAttribute>(), a => a.Template == "/privacy");
    }

    [Theory]
    [InlineData(false, false, false, false, false, "/login", PublicCtaKind.SignIn)]
    [InlineData(false, false, true, false, false, "/login", PublicCtaKind.SignIn)]
    [InlineData(false, false, true, true, false, "/register", PublicCtaKind.Register)]
    [InlineData(true, false, false, false, false, "/dashboard", PublicCtaKind.Dashboard)]
    [InlineData(true, false, true, true, true, "/admin/dashboard", PublicCtaKind.Dashboard)]
    [InlineData(false, true, true, true, false, "/change-password", PublicCtaKind.ChangePassword)]
    public void Cta_has_no_dead_links_and_respects_session(bool active, bool mustChange, bool enabled,
        bool routeExists, bool isAdmin, string href, PublicCtaKind kind)
    {
        var cta = PublicCta.Resolve(active, mustChange, enabled, routeExists, isAdmin);
        Assert.Equal(href, cta.Href);
        Assert.Equal(kind, cta.Kind);
        if (kind == PublicCtaKind.SignIn) Assert.Contains("sắp mở", cta.Note!);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string payload) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool HadAuthorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            HadAuthorization |= request.Headers.Authorization is not null;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/auth/registration-settings", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"registrationEnabled\":true}", true)]
    [InlineData(HttpStatusCode.OK, "{\"registrationEnabled\":false}", false)]
    [InlineData(HttpStatusCode.OK, "{}", false)]
    [InlineData(HttpStatusCode.OK, "not-json", false)]
    [InlineData(HttpStatusCode.NotFound, "{}", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", false)]
    public async Task Settings_is_public_fail_closed_and_cached(HttpStatusCode status, string body, bool expected)
    {
        var handler = new RecordingHandler(status, body);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.fixture/") };
        var client = new PublicSettingsClient(http);
        Assert.Equal(expected, await client.IsRegistrationEnabledAsync());
        Assert.Equal(expected, await client.IsRegistrationEnabledAsync());
        Assert.Equal(1, handler.Calls);
        Assert.False(handler.HadAuthorization);
    }

    [Fact]
    public void Landing_and_public_layout_do_not_use_business_api_clients()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "SmartCv.Web", "Components"));
        var landing = File.ReadAllText(Path.Combine(root, "Pages", "Landing.razor"));
        var layout = File.ReadAllText(Path.Combine(root, "Layout", "PublicLayout.razor"));
        foreach (var source in new[] { landing, layout })
        {
            Assert.DoesNotContain("@inject DashboardApiClient", source);
            Assert.DoesNotContain("@inject JobApiClient", source);
            Assert.DoesNotContain("@inject RunApiClient", source);
            Assert.Contains("PublicSettingsClient", source);
        }
        Assert.Contains("<PublicFooter />", layout);
        Assert.Contains("<FaqSection />", landing);
    }
}