extern alias web;

using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using web::SmartCv.Web.Components.Layout;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests;

public sealed class B51RoutingTests
{
    [Fact]
    public void Root_is_public_landing_and_dashboard_has_own_route()
    {
        var landingRoutes = typeof(Landing).GetCustomAttributes<RouteAttribute>()
            .Select(x => x.Template).ToArray();
        var dashboardRoutes = typeof(Home).GetCustomAttributes<RouteAttribute>()
            .Select(x => x.Template).ToArray();

        Assert.Contains("/", landingRoutes);
        Assert.Contains("/dashboard", dashboardRoutes);
        Assert.Contains("/recruitment", dashboardRoutes);
        Assert.DoesNotContain("/", dashboardRoutes);
        Assert.True(typeof(Landing).GetCustomAttributes(true).OfType<IAllowAnonymous>().Any());
        Assert.Equal(typeof(web::SmartCv.Web.Components.Layout.PublicLayout),
            typeof(Landing).GetCustomAttribute<LayoutAttribute>()?.LayoutType);
    }

    [Theory]
    [InlineData(null, "/dashboard")]
    [InlineData("", "/dashboard")]
    [InlineData("/", "/dashboard")]
    [InlineData("/login", "/dashboard")]
    [InlineData("https://evil.example", "/dashboard")]
    [InlineData("//evil.example", "/dashboard")]
    [InlineData("/jobs", "/jobs")]
    [InlineData("/dashboard", "/dashboard")]
    public void Return_url_falls_back_to_dashboard(string? input, string expected) =>
        Assert.Equal(expected, AuthText.SafeReturnUrl(input));

    [Fact]
    public void Explicit_dashboard_keeps_return_url_but_landing_does_not()
    {
        Assert.Equal("login?returnUrl=%2Fdashboard", AuthText.LoginUrl("/dashboard"));
        Assert.Equal("login", AuthText.LoginUrl("/"));
        Assert.Equal("login", AuthText.LoginUrl("https://evil.example"));
        Assert.Equal("login?returnUrl=%2Fdashboard&expired=1",
            AuthText.LoginUrl("/dashboard", expired: true));
        Assert.Equal("change-password?returnUrl=%2Fdashboard",
            AuthText.ChangePasswordUrl("/dashboard"));
    }

    [Fact]
    public void Filters_navigate_to_dashboard_not_landing()
    {
        Assert.Equal("/dashboard", new DashboardFilter().Url);
        Assert.Equal("/dashboard?jobId=3", new DashboardFilter(JobId: 3).Url);
    }

    [Fact]
    public void Sidebar_dashboard_link_is_exact_and_does_not_match_jobs()
    {
        var dashboard = AppNavigation.Groups.Single(x => x.Title == "Tổng quan")
            .Items.Single(i => i.Href == "dashboard");
        Assert.Equal("dashboard", dashboard.Href);
        Assert.Equal(NavLinkMatch.All, dashboard.Match);
    }
}