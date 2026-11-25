extern alias web;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.E2E;

/// <summary>
/// B57 browser E2E: real Chrome (Playwright .NET), real SmartCv.Web and SmartCv.Api on loopback Kestrel, synthetic
/// InMemory data and accounts only. Separate browser contexts per user: no shared cookies, storage or circuits.
/// </summary>
public sealed class B57BrowserTests
{
    private static readonly (int Width, int Height)[] Viewports = [(1366, 768), (1024, 768), (390, 844)];

    private sealed class Run : IAsyncDisposable
    {
        internal B57World World = null!;
        internal WebApplicationFactory<web::Program> Web = null!;
        internal HttpClient WebHttp = null!;
        internal IPlaywright Playwright = null!;
        internal IBrowser Browser = null!;
        internal string Root = "";
        internal readonly ConcurrentQueue<string> Errors = new();
        internal readonly List<string> Checks = [];
        internal string Stage = "start";
        internal string Artifacts = TestSupport.RepoFile("artifacts/b57/browser");

        internal static async Task<Run> StartAsync()
        {
            var run = new Run();
            run.World = await B57World.CreateAsync(kestrel: true);
            var apiRoot = run.World.Anonymous().BaseAddress!.ToString();
            run.Web = new WebApplicationFactory<web::Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development"); b.UseSetting("ApiBaseUrl", apiRoot);
                b.ConfigureTestServices(s => s.AddDataProtection().UseEphemeralDataProtectionProvider());
                b.ConfigureLogging(l => l.ClearProviders());
            });
            run.Web.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
            run.WebHttp = run.Web.CreateClient();
            run.Root = run.WebHttp.BaseAddress!.ToString();
            run.Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            run.Browser = await run.Playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = B43BrowserFact.Chrome() });
            Directory.CreateDirectory(run.Artifacts);
            return run;
        }

        internal async Task<(IBrowserContext Context, IPage Page)> NewUserAsync(int width = 1366, int height = 768)
        {
            var context = await Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height }, AcceptDownloads = true });
            var page = await context.NewPageAsync();
            // Explicit, condition-based waits; generous bound because the full suite runs ~4000 tests in parallel.
            page.SetDefaultTimeout(60000); page.SetDefaultNavigationTimeout(60000);
            page.Console += (_, m) => { if (m.Type == "error") Errors.Enqueue(m.Text); };
            page.PageError += (_, m) => Errors.Enqueue(m);
            return (context, page);
        }

        public async ValueTask DisposeAsync()
        {
            if (Browser is not null) await Browser.DisposeAsync();
            Playwright?.Dispose();
            WebHttp?.Dispose(); Web?.Dispose(); World?.Dispose();
        }
    }

    private static async Task LoginAsync(IPage page, string root, string email, string password)
    {
        await page.GotoAsync(root + "login");
        await page.Locator("#login-email").FillAsync(email);
        await page.Locator("#login-password").FillAsync(password);
        await page.Locator("button[type=submit]").ClickAsync();
        await page.WaitForURLAsync(url => !new Uri(url).AbsolutePath.StartsWith("/login", StringComparison.Ordinal));
    }

    private static Task<bool> NoHorizontalOverflow(IPage page) =>
        page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 1");

    private static async Task<string> SettledTextAsync(IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return await page.Locator("body").InnerTextAsync();
    }

    private static async Task Guard(Run run, Func<Task> body)
    {
        try { await body(); }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(run.Artifacts, "failure.json"), JsonSerializer.Serialize(new { run.Stage, Type = error.GetType().Name, run.Checks }));
            // Playwright messages can echo typed values; never copy credentials into test output.
            throw new Xunit.Sdk.XunitException("B57 browser failed at " + run.Stage + " (" + error.GetType().Name + "): " +
                error.Message.Split('\n')[0].Replace(run.World.Password, "***"));
        }
    }

    [B43BrowserFact]
    public async Task Owner_reports_downloads_direct_URLs_responsive_layout_and_logout_stay_inside_the_workspace()
    {
        await using var run = await Run.StartAsync();
        await Guard(run, async () =>
        {
            var w = run.World; var b = w.Data[B57World.OwnerBWorkspace];
            var (context, page) = await run.NewUserAsync();
            await using var _ = context;
            run.Stage = "login Owner A";
            await LoginAsync(page, run.Root, w.OwnerA.Email, w.Password);
            run.Checks.Add("Owner A login → " + new Uri(page.Url).AbsolutePath);

            run.Stage = "recruitment report";
            await page.GotoAsync(run.Root + "reports/recruitment");
            await page.Locator(".hrr-table tbody tr").First.WaitForAsync();
            var report = await SettledTextAsync(page);
            Assert.Contains("B57_OWNER_A_PRIVATE job", report);
            Assert.DoesNotContain("B57_OWNER_B_PRIVATE", report); Assert.DoesNotContain("B57_INTERNAL_PRIVATE", report);
            Assert.Equal(2, await page.Locator(".hrr-table tbody tr").CountAsync());
            run.Checks.Add("report rows = 2, only Owner A");

            run.Stage = "Excel download";
            await page.GetByRole(AriaRole.Button, new() { Name = "Xuất Excel", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Dialog).WaitForAsync();
            var downloadTask = page.WaitForDownloadAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Xác nhận & tải file", Exact = true }).ClickAsync();
            var download = await downloadTask;
            Assert.EndsWith(".xlsx", download.SuggestedFilename);
            using (var book = new XLWorkbook(new MemoryStream(await File.ReadAllBytesAsync((await download.PathAsync())!))))
            {
                var cells = book.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()).ToList();
                Assert.Contains(cells, c => c.Contains("B57_OWNER_A_PRIVATE job"));
                Assert.DoesNotContain(cells, c => c.Contains("B57_OWNER_B_PRIVATE") || c.Contains("B57_INTERNAL_PRIVATE"));
                Assert.DoesNotContain(cells, c => c.Contains("candidate 0")); // anonymous by default
                Assert.Equal(3, book.Worksheet("Ho so").LastRowUsed()!.RowNumber());
            }
            await page.GetByRole(AriaRole.Dialog).WaitForAsync(new() { State = WaitForSelectorState.Detached });
            run.Checks.Add("browser Excel download parsed: Owner A rows only, anonymous");

            run.Stage = "direct foreign URLs";
            foreach (var path in new[] { $"jobs/{b.Job}", $"candidates/{b.Candidates[0]}", $"runs/{b.Run}", $"results/evaluations/{b.Evaluations[0]}", $"results?runId={b.Run}" })
            {
                await page.GotoAsync(run.Root + path);
                var text = await SettledTextAsync(page);
                Assert.DoesNotContain("B57_OWNER_B_PRIVATE", text);
                Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync(), path + " crashed the circuit");
            }
            run.Checks.Add("Owner B direct URLs show no Owner B data and no crash");

            run.Stage = "admin pages for owner";
            foreach (var path in new[] { "admin/reports", "users", "admin/activity-logs" })
            {
                await page.GotoAsync(run.Root + path);
                await page.Locator("#denied-title").WaitForAsync();
                Assert.Equal(0, await page.Locator(".audit-list, .users-table, [data-admin-report]").CountAsync());
            }
            Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Audit Log", Exact = true }).CountAsync());
            Assert.Equal(0, await page.Locator("a[href='users']").CountAsync());   // no "Quản lý tài khoản" for the Owner
            run.Checks.Add("Owner cannot open admin pages; admin menu hidden");

            run.Stage = "responsive";
            foreach (var path in new[] { "reports/recruitment", "reports", "results", "jobs" })
                foreach (var (width, height) in Viewports)
                {
                    await page.SetViewportSizeAsync(width, height);
                    await page.GotoAsync(run.Root + path);
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    Assert.True(await NoHorizontalOverflow(page), $"Horizontal overflow on /{path} at {width}x{height}");
                    if (path == "reports/recruitment") await page.ScreenshotAsync(new() { Path = Path.Combine(run.Artifacts, $"owner-report-{width}.png"), FullPage = true });
                }
            await page.SetViewportSizeAsync(390, 844);
            await page.GotoAsync(run.Root + "reports/recruitment");
            await page.GetByRole(AriaRole.Button, new() { Name = "Xuất PDF", Exact = true }).ClickAsync();
            var dialogBox = await page.GetByRole(AriaRole.Dialog).BoundingBoxAsync();
            Assert.NotNull(dialogBox);
            Assert.True(dialogBox!.X >= 0 && dialogBox.X + dialogBox.Width <= 390 + 1 && dialogBox.Height <= 844, "Export dialog overflows the 390px viewport");
            await page.Keyboard.PressAsync("Escape");
            await page.GetByRole(AriaRole.Dialog).WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await page.SetViewportSizeAsync(1366, 768);
            run.Checks.Add("1366x768, 1024x768, 390x844 without horizontal overflow; dialog fits mobile");

            run.Stage = "logout cache";
            await page.GotoAsync(run.Root + "reports/recruitment");
            await page.Locator(".hrr-table tbody tr").First.WaitForAsync();
            var logout = page.GetByRole(AriaRole.Button, new() { Name = "Đăng xuất" });
            if (await logout.CountAsync() == 0 || !await logout.First.IsVisibleAsync())
            {
                await page.Locator("[data-user-menu] button, .user-menu button, button[aria-haspopup]").First.ClickAsync();
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Đăng xuất" }).First.ClickAsync();
            await page.WaitForURLAsync(u => new Uri(u).AbsolutePath.StartsWith("/login", StringComparison.Ordinal) || new Uri(u).AbsolutePath == "/");
            await page.GoBackAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            Assert.DoesNotContain("B57_OWNER_A_PRIVATE", await page.Locator("body").InnerTextAsync());
            await page.GotoAsync(run.Root + "reports/recruitment");
            await page.WaitForURLAsync(u => new Uri(u).AbsolutePath.StartsWith("/login", StringComparison.Ordinal));
            Assert.True(await page.EvaluateAsync<bool>("!Object.values(sessionStorage).some(v=>v.startsWith('eyJ')) && !Object.keys(localStorage).some(k=>/token|jwt|auth/i.test(k))"));
            run.Checks.Add("logout: back button and direct report URL show no cached workspace data");

            Assert.True(run.Errors.IsEmpty, "Browser console errors: " + string.Join(" | ", run.Errors.Take(3)));
        });
        await File.WriteAllTextAsync(Path.Combine(run.Artifacts, "owner-results.json"), JsonSerializer.Serialize(new { Status = "PASS", run.Checks }));
    }

    [B43BrowserFact]
    public async Task Roles_navigation_anonymous_redirect_and_keyboard_only_flows()
    {
        await using var run = await Run.StartAsync();
        await Guard(run, async () =>
        {
            var w = run.World;
            run.Stage = "anonymous";
            var (anonContext, anon) = await run.NewUserAsync();
            await using (anonContext)
            {
                foreach (var path in new[] { "reports", "reports/recruitment", "admin/reports", "jobs" })
                {
                    await anon.GotoAsync(run.Root + path);
                    await anon.WaitForURLAsync(u => new Uri(u).AbsolutePath.StartsWith("/login", StringComparison.Ordinal));
                    Assert.DoesNotContain("B57_", await anon.Locator("body").InnerTextAsync());
                }
            }
            run.Checks.Add("anonymous → /login for reports, admin and business pages");

            run.Stage = "keyboard login HR";
            var (hrContext, hr) = await run.NewUserAsync();
            await using (hrContext)
            {
                await hr.GotoAsync(run.Root + "login");
                await hr.Locator("#login-email").WaitForAsync();
                for (var i = 0; i < 30 && await hr.EvaluateAsync<string>("document.activeElement?.id ?? ''") != "login-email"; i++) await hr.Keyboard.PressAsync("Tab");
                Assert.Equal("login-email", await hr.EvaluateAsync<string>("document.activeElement.id"));
                Assert.Equal("Email", (await hr.Locator("label[for=login-email]").InnerTextAsync()).Trim());
                await hr.Keyboard.TypeAsync(w.InternalHr.Email);
                await hr.Keyboard.PressAsync("Tab");
                Assert.Equal("login-password", await hr.EvaluateAsync<string>("document.activeElement.id"));
                await hr.Keyboard.TypeAsync("wrong-" + Guid.NewGuid().ToString("N"));
                await hr.Keyboard.PressAsync("Enter");
                await hr.Locator("#login-error").WaitForAsync();
                Assert.Equal("alert", await hr.Locator("#login-error").GetAttributeAsync("role"));
                await hr.Locator("#login-password").FillAsync(w.Password);
                await hr.Locator("#login-password").FocusAsync();
                await hr.Keyboard.PressAsync("Enter");
                await hr.WaitForURLAsync(u => !new Uri(u).AbsolutePath.StartsWith("/login", StringComparison.Ordinal));
                run.Checks.Add("keyboard login: tab order email→password, labelled inputs, role=alert error");

                run.Stage = "keyboard export dialog";
                await hr.GotoAsync(run.Root + "reports/recruitment");
                await hr.Locator(".hrr-table tbody tr").First.WaitForAsync();
                var excel = hr.GetByRole(AriaRole.Button, new() { Name = "Xuất Excel", Exact = true });
                await excel.FocusAsync();
                await hr.Keyboard.PressAsync("Enter");
                await hr.GetByRole(AriaRole.Dialog).WaitForAsync();
                await hr.WaitForFunctionAsync("document.activeElement?.closest('[role=dialog]') !== null");
                for (var i = 0; i < 6; i++)
                {
                    await hr.Keyboard.PressAsync("Tab");
                    Assert.True(await hr.EvaluateAsync<bool>("document.activeElement?.closest('[role=dialog]') !== null"), "Tab left the modal export dialog");
                }
                await hr.Keyboard.PressAsync("Escape");
                await hr.GetByRole(AriaRole.Dialog).WaitForAsync(new() { State = WaitForSelectorState.Detached });
                await hr.WaitForFunctionAsync("document.activeElement?.textContent?.trim() === 'Xuất Excel'");
                await hr.Keyboard.PressAsync("Enter");
                await hr.GetByRole(AriaRole.Dialog).WaitForAsync();
                var confirm = hr.GetByRole(AriaRole.Button, new() { Name = "Xác nhận & tải file", Exact = true });
                for (var i = 0; i < 6 && !await confirm.EvaluateAsync<bool>("e => e === document.activeElement"); i++) await hr.Keyboard.PressAsync("Tab");
                var downloadTask = hr.WaitForDownloadAsync();
                await hr.Keyboard.PressAsync("Enter");
                var download = await downloadTask;
                using (var book = new XLWorkbook(new MemoryStream(await File.ReadAllBytesAsync((await download.PathAsync())!))))
                {
                    var cells = book.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()).ToList();
                    Assert.Contains(cells, c => c.Contains("B57_INTERNAL_PRIVATE job"));
                    Assert.DoesNotContain(cells, c => c.Contains("B57_OWNER_"));
                }
                run.Checks.Add("keyboard-only export: focus enters dialog, Tab trapped, Escape restores focus, Enter downloads");

                run.Stage = "HR navigation";
                Assert.Equal(0, await hr.GetByRole(AriaRole.Link, new() { Name = "Audit Log", Exact = true }).CountAsync());
                await hr.GotoAsync(run.Root + "admin/reports");
                await hr.Locator("#denied-title").WaitForAsync();
                await hr.GotoAsync(run.Root + "reports");
                var center = await SettledTextAsync(hr);
                Assert.DoesNotContain("Mở báo cáo quản trị", center);
                run.Checks.Add("HR: admin report denied, reports center shows recruitment cards only");
            }

            run.Stage = "Admin navigation";
            var (adminContext, admin) = await run.NewUserAsync();
            await using (adminContext)
            {
                await LoginAsync(admin, run.Root, w.Admin.Email, w.Password);
                await admin.GotoAsync(run.Root + "admin/reports");
                await admin.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.Equal(0, await admin.Locator("#denied-title").CountAsync());
                var adminText = await admin.Locator("body").InnerTextAsync();
                Assert.DoesNotContain("B57_", adminText);
                foreach (var path in new[] { "reports/recruitment", "jobs", "candidates", "results" })
                {
                    await admin.GotoAsync(run.Root + path);
                    var text = await SettledTextAsync(admin);
                    var path2 = new Uri(admin.Url).AbsolutePath;
                    Assert.True(await admin.Locator("#denied-title").CountAsync() > 0 || path2.StartsWith("/admin", StringComparison.Ordinal),
                        $"Admin reached business page /{path} (now {path2})");
                    Assert.DoesNotContain("B57_", text);
                }
                Assert.Equal(0, await admin.GetByRole(AriaRole.Link, new() { Name = "Tải CV", Exact = false }).CountAsync());
                // Real API: the Admin sidebar opens the existing account console, which loads real (synthetic) accounts.
                await admin.GotoAsync(run.Root + "admin/dashboard");
                await admin.GetByRole(AriaRole.Link, new() { Name = "Quản lý tài khoản", Exact = true }).ClickAsync();
                await admin.WaitForURLAsync(run.Root + "users");
                await admin.GetByText(w.InternalHr.Email, new() { Exact = false }).First.WaitForAsync();
                foreach (var (width, height) in Viewports)
                {
                    await admin.SetViewportSizeAsync(width, height);
                    await admin.GotoAsync(run.Root + "admin/reports");
                    await admin.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    Assert.True(await NoHorizontalOverflow(admin), $"Horizontal overflow on /admin/reports at {width}x{height}");
                }
                run.Checks.Add("Admin: operations report OK, business pages denied/redirected, no business data, responsive");
            }
            Assert.True(run.Errors.IsEmpty, "Browser console errors: " + string.Join(" | ", run.Errors.Take(3)));
        });
        await File.WriteAllTextAsync(Path.Combine(run.Artifacts, "roles-results.json"), JsonSerializer.Serialize(new { Status = "PASS", run.Checks }));
    }
}
