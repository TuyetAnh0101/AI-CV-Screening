extern alias web;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests;

public class B30AdminBrowserTests
{
    private static readonly string[] BusinessApi = ["/api/dashboard", "/api/runs", "/api/cvs", "/api/jobs", "/api/candidates", "/api/prompts", "/api/evaluation-sets", "/api/applications"];
    private static readonly string[] AdminApi = ["/api/admin/", "/api/users", "/api/exports"];

    /// <summary>
    /// Since #114/#116/#120 the System Admin and the HR have SEPARATE pages: the Admin gets the operations dashboard
    /// (/admin/dashboard, aggregate /api/admin/ops/* only) and no business page; the HR gets the recruitment dashboard and no admin page.
    /// </summary>
    [B43BrowserFact]
    public async Task Admin_and_HR_have_separate_dashboards_and_pages_with_real_browser_states()
    {
        var api = new B30Api();
        using var factory = new WebApplicationFactory<web::Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development"); b.UseSetting("ApiBaseUrl", "https://synthetic-b30.invalid/");
            b.ConfigureLogging(l => l.ClearProviders());
            b.ConfigureTestServices(s =>
            {
                s.AddDataProtection().UseEphemeralDataProtectionProvider();
                s.ConfigureHttpClientDefaults(c => c.ConfigurePrimaryHttpMessageHandler(() => api.Handler));
            });
        });
        factory.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        using var http = factory.CreateClient(); var root = http.BaseAddress!.ToString();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = B43BrowserFact.Chrome() });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1366, Height = 900 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(20000);
        var errors = new ConcurrentQueue<string>(); page.PageError += (_, error) => errors.Enqueue(error);
        var artifacts = TestSupport.RepoFile("artifacts/b30-ui"); Directory.CreateDirectory(artifacts);
        try
        {
            await Login(page, root, "admin");
            await page.WaitForURLAsync(root + "admin/dashboard");
            var kpis = page.Locator(".op-kpis");
            await kpis.WaitForAsync();
            Assert.Contains("35", await kpis.InnerTextAsync());          // accounts
            Assert.Contains("120", await kpis.InnerTextAsync());         // stored CVs (count only)
            // The AI history reader is independent: its outage is reported without blanking the overview.
            await page.GetByText("Lịch sử AI chưa tải được", new() { Exact = false }).WaitForAsync();
            foreach (var width in new[] { 360, 390, 768, 1024, 1366, 1920 })
            {
                await page.SetViewportSizeAsync(width, 900);
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"), "Horizontal overflow at " + width);
                if (width is 390 or 1366) await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "admin-" + width + ".png"), FullPage = true });
            }
            await page.SetViewportSizeAsync(390, 844);
            await page.GetByRole(AriaRole.Button, new() { Name = "Mở menu", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Dashboard quản trị", Exact = true }).WaitForAsync();
            Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Tải CV lên", Exact = true }).CountAsync());   // no business menu
            // Account management is in the Admin sidebar and opens the existing /users console; refresh keeps it.
            await page.GetByRole(AriaRole.Link, new() { Name = "Quản lý tài khoản", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(root + "users");
            await page.GetByRole(AriaRole.Heading, new() { Name = "Quản lý tài khoản", Exact = true }).WaitForAsync();
            await page.SetViewportSizeAsync(1366, 900);
            await page.ReloadAsync(); await page.GetByRole(AriaRole.Heading, new() { Name = "Quản lý tài khoản", Exact = true }).WaitForAsync();
            Assert.Equal(root + "users", page.Url);
            Assert.Equal("nav-item active", await page.Locator("#main-navigation a[href='users']").GetAttributeAsync("class"));
            await page.GotoAsync(root + "admin/dashboard"); await page.Locator(".op-kpis").WaitForAsync();

            // Business pages are closed to the Admin; the HR home /dashboard sends the Admin to its own dashboard (B51).
            await page.GotoAsync(root + "upload"); await page.Locator("#denied-title").WaitForAsync();
            await page.GotoAsync(root + "dashboard?jobId=3"); await page.WaitForURLAsync(root + "admin/dashboard");
            await kpis.WaitForAsync();

            api.OverviewUnavailable = true;
            await page.GetByRole(AriaRole.Button, new() { Name = "↻ Làm mới", Exact = true }).ClickAsync();
            await page.GetByText("Không thể tải số liệu toàn hệ thống.", new() { Exact = true }).WaitForAsync();
            Assert.Equal(0, await kpis.CountAsync());                    // never a fake zero
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "admin-outage.png"), FullPage = true });
            api.OverviewUnavailable = false;
            await page.GetByRole(AriaRole.Button, new() { Name = "Thử lại", Exact = true }).ClickAsync();
            await kpis.WaitForAsync();
            Assert.Contains("120", await kpis.InnerTextAsync());
            await page.GotoAsync(root + "admin/activity-logs"); await page.Locator("#activity-title").WaitForAsync();
            Assert.DoesNotContain(api.Handler.Calls, c => c.Authorization == "Bearer Admin" && BusinessApi.Any(c.PathAndQuery.StartsWith));
            // Sign out and in again: the Admin menu (with account management) comes back intact.
            await page.GetByRole(AriaRole.Button, new() { Name = "Đăng xuất", Exact = true }).First.ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/login");
            Assert.Equal(0, await page.Locator("a[href='users']").CountAsync());
            await Login(page, root, "admin"); await page.WaitForURLAsync(root + "admin/dashboard");
            Assert.Equal(1, await page.Locator("#main-navigation a[href='users']").CountAsync());

            foreach (var role in new[] { "hr", "mcp", "candidate", "anonymous" })
            {
                await using var other = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
                var tab = await other.NewPageAsync(); tab.SetDefaultTimeout(20000);
                tab.PageError += (_, error) => errors.Enqueue(role + ": " + error);
                if (role != "anonymous") await Login(tab, root, role);
                if (role == "hr")
                {
                    await tab.WaitForURLAsync(root + "dashboard");
                    await tab.Locator("#main-navigation").WaitForAsync(new() { State = WaitForSelectorState.Attached });
                    await tab.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "hr-390.png"), FullPage = true });
                    Assert.Equal(0, await tab.Locator("a[href='users']").CountAsync());   // no account management for HR
                    await tab.GotoAsync(root + "upload"); await tab.Locator("#upload-title").WaitForAsync();
                }
                var calls = api.Handler.Calls.Count;
                await tab.GotoAsync(root + "admin/dashboard");
                if (role is "hr" or "candidate") await tab.Locator("#denied-title").WaitForAsync();
                else if (role == "anonymous") await tab.WaitForURLAsync(root + "login?*");
                else await tab.WaitForURLAsync(root + "change-password?*");
                Assert.DoesNotContain(api.Handler.Calls.Skip(calls), c => AdminApi.Any(c.PathAndQuery.StartsWith));
            }
            Assert.Empty(errors);
            Assert.All(api.Handler.Calls.Where(c => c.Method != "GET"), c => Assert.Equal("/api/auth/login", c.PathAndQuery));
        }
        catch
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "failure.png"), FullPage = true });
            throw;
        }
    }

    private static async Task Login(IPage page, string root, string role)
    {
        await page.GotoAsync(root + "login");
        await page.Locator("#login-email").FillAsync(role + "@example.invalid");
        await page.Locator("#login-password").FillAsync("SyntheticOnly123!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Đăng nhập", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => !new Uri(url).AbsolutePath.Equals("/login"));
    }
}
