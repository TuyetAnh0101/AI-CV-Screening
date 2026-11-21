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
    [B43BrowserFact]
    public async Task Admin_and_HR_share_business_pages_but_have_separate_dashboards_with_real_browser_states()
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
            await page.Locator(".admin-dashboard[data-interactive=true][aria-busy=false]").WaitForAsync();
            Assert.Contains("35", await page.Locator("[data-kpi=accounts]").InnerTextAsync());
            Assert.Contains("120", await page.Locator("[data-kpi=cvs]").InnerTextAsync());
            Assert.Contains("Chưa xác minh", await page.Locator(".ai-status").InnerTextAsync());
            await page.GetByText("API đã phản hồi · có phụ thuộc chưa sẵn sàng", new() { Exact = true }).WaitForAsync();
            foreach (var width in new[] { 360, 390, 768, 1024, 1366, 1920 })
            {
                await page.SetViewportSizeAsync(width, 900);
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"), "Horizontal overflow at " + width);
                if (width is 390 or 1366) await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "admin-" + width + ".png"), FullPage = true });
            }
            await page.SetViewportSizeAsync(390, 844);
            await page.GetByRole(AriaRole.Button, new() { Name = "Mở menu", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Dashboard quản trị", Exact = true }).WaitForAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Tải hồ sơ lên", Exact = true }).ClickAsync();
            await page.Locator("#upload-title").WaitForAsync(); Assert.Contains("/upload", page.Url);
            await page.SetViewportSizeAsync(1366, 900);
            await page.GotoAsync(root + "admin/dashboard");
            await page.Locator(".admin-dashboard[data-interactive=true][aria-busy=false]").WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Trang xuất sau", Exact = true }).ClickAsync();
            await page.GetByText("Chuyên viên kiểm thử · Trang thứ hai", new() { Exact = true }).WaitForAsync();
            api.HoldSummary = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.GetByRole(AriaRole.Button, new() { Name = "Làm mới", Exact = true }).ClickAsync();
            await page.Locator(".admin-dashboard[aria-busy=true]").WaitForAsync();
            Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Đang cập nhật…", Exact = true }).IsDisabledAsync());
            api.HoldSummary.TrySetResult(); api.HoldSummary = null;
            await page.Locator(".admin-dashboard[aria-busy=false]").WaitForAsync();
            api.SummaryUnavailable = true; api.EmptyJournals = true;
            await page.GetByRole(AriaRole.Button, new() { Name = "Làm mới", Exact = true }).ClickAsync();
            await page.GetByText("Chưa tải được tổng hợp tuyển dụng.", new() { Exact = true }).WaitForAsync();
            await page.GetByText("Chưa có lần xuất trong khoảng này.", new() { Exact = true }).WaitForAsync();
            Assert.Equal("—", await page.Locator("[data-kpi=cvs]>strong").InnerTextAsync());
            Assert.Equal("35", await page.Locator("[data-kpi=accounts]>strong").InnerTextAsync());
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "admin-partial-outage.png"), FullPage = true });
            api.SummaryUnavailable = false;
            await page.GetByRole(AriaRole.Button, new() { Name = "Thử lại", Exact = true }).ClickAsync();
            await page.Locator(".admin-dashboard[aria-busy=false]").WaitForAsync();
            Assert.Equal("120", await page.Locator("[data-kpi=cvs]>strong").InnerTextAsync());
            await page.GetByRole(AriaRole.Link, new() { Name = "Tổng quan tuyển dụng", Exact = true }).ClickAsync();
            await page.Locator(".dashboard[data-interactive=true]").WaitForAsync(); Assert.EndsWith("/recruitment", page.Url);
            await page.GetByRole(AriaRole.Button, new() { Name = "7 ngày", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(root + "recruitment?*");
            await page.GetByRole(AriaRole.Button, new() { Name = "Xóa bộ lọc", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(root + "recruitment");
            await page.ReloadAsync(); await page.Locator(".dashboard[data-interactive=true]").WaitForAsync();
            Assert.EndsWith("/recruitment", page.Url);
            await page.GotoAsync(root + "results"); await page.GetByRole(AriaRole.Heading, new() { Name = "Kết quả & xếp hạng", Exact = true }).WaitForAsync();
            await page.GotoAsync(root + "admin/activity-logs"); await page.Locator("#activity-title").WaitForAsync();
            foreach (var role in new[] { "hr", "mcp", "candidate", "anonymous" })
            {
                await using var other = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
                var tab = await other.NewPageAsync(); tab.SetDefaultTimeout(20000);
                if (role != "anonymous") await Login(tab, root, role);
                if (role == "hr")
                {
                    await tab.Locator(".dashboard[data-interactive=true]").WaitForAsync(); Assert.Equal(root + "dashboard", tab.Url);
                    await tab.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "hr-390.png"), FullPage = true });
                    await tab.GotoAsync(root + "upload"); await tab.Locator("#upload-title").WaitForAsync();
                }
                var calls = api.Handler.Calls.Count;
                await tab.GotoAsync(root + "admin/dashboard");
                if (role is "hr" or "candidate") await tab.Locator("#denied-title").WaitForAsync();
                else if (role == "anonymous") await tab.WaitForURLAsync(root + "login?*");
                else await tab.WaitForURLAsync(root + "change-password?*");
                Assert.DoesNotContain(api.Handler.Calls.Skip(calls), c => c.PathAndQuery.StartsWith("/api/users?") || c.PathAndQuery.StartsWith("/api/admin/audit-events?") || c.PathAndQuery.StartsWith("/api/exports?"));
            }
            Assert.Empty(errors);
            Assert.All(api.Handler.Calls.Where(c => c.Method != "GET"), c => Assert.Equal("/api/auth/login", c.PathAndQuery));
        }
        catch
        {
            api.HoldSummary?.TrySetResult();
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
