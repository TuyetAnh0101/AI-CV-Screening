extern alias web;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using ClosedXML.Excel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SmartCv.Core.Backup;
using SmartCv.Infrastructure.Backup;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Ops;

/// <summary>Real web, real API, real SQL catalog and the real worker coordinator; Google is faked at the HTTP edge.</summary>
public sealed class A62AdminBrowserTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    [A62SqlFact]
    public async Task Admin_connects_drive_backs_up_retries_reschedules_restores_reports_and_disconnects()
    {
        var google = new FakeGoogleHandler();
        var webPort = FreePort();
        var webAddress = $"http://127.0.0.1:{webPort}/";
        await using var it = await A62Installation.CreateAsync(new()
        {
            ["Backup:GoogleDrive:ClientId"] = "test-client.apps.googleusercontent.com", ["Backup:GoogleDrive:ClientSecret"] = "test-secret",
            ["App:WebBaseUrl"] = webAddress
        }, kestrel: true, hooks: h => h.Services.AddHttpClient(GoogleDriveStoreFactory.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => google).SetHandlerLifetime(Timeout.InfiniteTimeSpan));
        await it.Execute("DELETE FROM dbo.BackupDriveConnections", it.Catalog);

        using var api = it.World.Anonymous();
        using var webHost = new WebApplicationFactory<web::Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development"); b.UseSetting("ApiBaseUrl", api.BaseAddress!.ToString());
            b.ConfigureTestServices(s => s.AddDataProtection().UseEphemeralDataProtectionProvider());
            b.ConfigureLogging(l => l.ClearProviders());
        });
        webHost.UseKestrel(o => o.Listen(IPAddress.Loopback, webPort)); using var webClient = webHost.CreateClient();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = B43BrowserFact.Chrome() });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1366, Height = 900 }, AcceptDownloads = true });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        // The consent screen: Google redirects back to the API callback with a code and the unchanged state.
        await page.RouteAsync("https://accounts.google.com/**", async route =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(route.Request.Url).Query);
            await route.FulfillAsync(new() { Status = 302, Headers = new Dictionary<string, string> { ["Location"] = query["redirect_uri"] + "?code=good-code&state=" + Uri.EscapeDataString(query["state"]!) } });
        });
        var shots = TestSupport.RepoFile("artifacts/a62/browser"); Directory.CreateDirectory(shots);
        Task Shot(string name) => page.ScreenshotAsync(new() { Path = Path.Combine(shots, name + ".png"), FullPage = true });
        async Task Tab(string key) => await page.Locator($"[data-tab='{key}']").ClickAsync();
        async Task Tick() { await it.Tick(); await it.Tick(BackupWorkerRoles.Restore); }
        async Task RequestAndRun(string action)
        {
            await page.Locator($"[data-action='{action}']").ClickAsync();
            await page.Locator("[data-confirm]").ClickAsync();
            await page.Locator("[data-activity]").WaitForAsync();
            await Tick();
            await page.Locator("[data-activity]").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        }

        await page.GotoAsync(webAddress + "login");
        await page.Locator("#login-email").FillAsync(it.World.Admin.Email);
        await page.Locator("#login-password").FillAsync(it.World.Password);
        await page.Locator("button[type=submit]").ClickAsync();
        await page.WaitForURLAsync(url => !new Uri(url).AbsolutePath.StartsWith("/login", StringComparison.Ordinal));

        // Nothing installed: the page states exactly what is missing and offers no working-looking buttons.
        await page.GotoAsync(webAddress + "admin/backups");
        await page.Locator("[data-blocker='drive-not-connected']").WaitForAsync();
        await page.Locator("[data-blocker='worker-missing']").WaitForAsync();
        await Tab("schedule");
        foreach (var action in new[] { "full", "log", "verify", "restore-drill" }) Assert.True(await page.Locator($"[data-action='{action}']").IsDisabledAsync(), action);

        // Connect Drive through the OAuth round trip.
        await Tab("drive");
        await page.Locator("[data-action='connect']").ClickAsync();
        await page.WaitForURLAsync("**/admin/backups?drive=connected");
        await page.GetByText(google.Email).First.WaitForAsync();
        await using (var db = it.Db())
        {
            var connection = await db.BackupDriveConnections.SingleAsync();
            Assert.DoesNotContain(google.RefreshToken, connection.ProtectedCredential);
            Assert.DoesNotContain("test-secret", connection.ProtectedCredential);
        }
        await page.Locator("[data-action='check']").ClickAsync();
        await page.Locator("[data-drive-check='ok']").WaitForAsync();
        await Shot("admin-drive");

        // Workers appear; the first backup runs on demand and is listed from the catalog.
        await Tick();
        await page.ReloadAsync();
        await Tab("schedule");
        await page.Locator("[data-action='full']:not([disabled])").WaitForAsync();
        await RequestAndRun("full");
        await Tab("points");
        await page.Locator("[data-point='Full']").WaitForAsync();
        await Shot("admin-points");

        // Failure shows its cause; retry after the fix succeeds.
        var cv = Directory.GetFiles(Path.Combine(it.CvBase, "Uploads"), "*.pdf", SearchOption.AllDirectories)[0];
        File.Move(cv, cv + ".away");
        await Tab("schedule"); await RequestAndRun("full");
        await Tab("overview"); await page.Locator("[data-warning='full-failed']").WaitForAsync();
        await Tab("report");
        await page.Locator("tr[data-run='Failed'][data-job='full']").WaitForAsync();
        Assert.Contains("A62_FILES", await page.Locator("tr[data-run='Failed'][data-job='full']").InnerTextAsync());
        // A missing file needs a person: no retry button, the row says what to do; after the fix, back up again.
        Assert.Equal(0, await page.Locator("[data-retry='full']").CountAsync());
        Assert.Contains("cv-report", await page.Locator("tr[data-run='Failed'][data-job='full'] [data-guidance='A62_FILES']").InnerTextAsync());
        File.Move(cv + ".away", cv);
        await Tab("schedule"); await RequestAndRun("full");

        // Schedule change is applied and the next run follows it.
        await page.Locator("[data-policy='time']").FillAsync("22:15");
        await page.Locator("[data-policy='keep']").FillAsync("10");
        await page.Locator("[data-policy='keep']").BlurAsync();
        await page.Locator("[data-action='settings']").ClickAsync();
        await page.Locator("[data-confirm]").ClickAsync();
        await page.Locator("[data-notice='ok']").WaitForAsync();
        await using (var db = it.Db()) { var s = await db.BackupSettings.SingleAsync(); Assert.Equal(("22:15", 10), (s.FullTimeLocal, s.RetentionCount)); }
        await Tab("overview");
        Assert.Contains("22:15", await page.Locator("[data-next-run]").InnerTextAsync());
        // Connected, running, ready and backed up are separate facts; the drill has not been asked for by the Admin yet.
        Assert.Equal(6, await page.Locator("[data-step]").CountAsync());
        foreach (var step in new[] { "drive", "running", "ready", "backup" })
            Assert.Equal("ok", await page.Locator($"[data-step='{step}']").GetAttributeAsync("data-state"));
        await Shot("admin-overview");

        // Restore drill and a controlled recovery that needs the typed confirmation.
        await Tab("schedule"); await RequestAndRun("restore-drill");
        await Tab("points");
        await page.Locator("[data-restore]").First.ClickAsync();
        Assert.True(await page.Locator("[data-confirm-restore]").IsDisabledAsync());
        await page.Locator("[data-confirm-text]").FillAsync("KHÔI PHỤC");
        await page.Locator("[data-confirm-restore]").ClickAsync();
        await page.Locator("[data-activity]").WaitForAsync();
        await Tick();
        await page.Locator("[data-activity]").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await Tab("report");
        await page.Locator("tr[data-run='Succeeded'][data-job='recovery']").WaitForAsync();
        Assert.Contains("SmartCv_Restore_", await page.Locator("tr[data-job='recovery']").InnerTextAsync());
        // The restore worker ran its first weekly drill by itself, plus the one the Admin asked for.
        var drills = await page.Locator("tr[data-run='Succeeded'][data-job='restore-drill']").AllInnerTextsAsync();
        Assert.Contains(drills, d => d.Contains("Theo lịch")); Assert.Contains(drills, d => d.Contains("Admin yêu cầu"));

        // A long history is paged.
        await it.Execute("INSERT INTO dbo.BackupJobs (BackupJobId,Type,[Trigger],Status,RequestedAtUtc,FinishedAtUtc,Attempt,Progress,ErrorCode) " +
            "SELECT TOP 30 NEWID(),'full','scheduled','Missed',SYSUTCDATETIME(),SYSUTCDATETIME(),1,0,'A62_MISSED' FROM sys.all_objects", it.Catalog);
        await page.Locator("[data-days='30']").ClickAsync();
        var pager = page.Locator("[data-pager='jobs']");
        await pager.WaitForAsync();
        Assert.Equal(25, await page.Locator("tr[data-run]").CountAsync());
        await pager.Locator("[data-page='next']").ClickAsync();
        await pager.GetByText("Trang 2 /").WaitForAsync();
        Assert.InRange(await page.Locator("tr[data-run]").CountAsync(), 1, 25);

        // Report filters and Excel export.
        await page.Locator("[data-days='7']").ClickAsync();
        await page.Locator("[data-filter='status']").SelectOptionAsync("Failed");
        await page.Locator("[data-apply]").ClickAsync();
        await page.Locator("tr[data-run='Succeeded']").First.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        // The first page may hold only other results already: wait until the filtered list itself has arrived.
        await page.Locator("tr[data-run='Missed']").First.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.Locator("tr[data-run='Failed']").First.WaitForAsync();
        var failedRows = await page.Locator("tr[data-run='Failed']").CountAsync(); Assert.True(failedRows >= 1);
        var download = await page.RunAndWaitForDownloadAsync(() => page.Locator("[data-export]").ClickAsync());
        var file = Path.Combine(it.Root, "report.xlsx"); await download.SaveAsAsync(file);
        using (var book = new XLWorkbook(file))
        {
            Assert.Equal(failedRows, book.Worksheet("Lịch sử").RowsUsed().Count() - 1);
            var text = string.Join('\n', book.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()));
            foreach (var secret in new[] { it.Root, google.RefreshToken, "test-secret", it.World.Admin.Email, "B57_OWNER_A_PRIVATE" }) Assert.DoesNotContain(secret, text);
        }
        foreach (var (width, height) in new[] { (1366, 768), (390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var key in new[] { "overview", "drive", "points", "schedule", "report" })
            {
                await Tab(key);
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 1"), $"Overflow {key} at {width}");
                if (width == 390) await Shot("admin-mobile-" + key);
            }
        }
        await page.SetViewportSizeAsync(1366, 900);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());

        // The browser never saw secrets.
        var html = await page.ContentAsync();
        Assert.DoesNotContain(google.RefreshToken, html); Assert.DoesNotContain("test-secret", html);

        // Disconnect: credential removed and revoked, files on Drive untouched, backup disabled again.
        var stored = it.Cloud.Files.Count;
        await Tab("drive");
        await page.Locator("[data-action='disconnect']").ClickAsync();
        await page.Locator("[data-confirm]").ClickAsync();
        await page.Locator("[data-action='connect']").WaitForAsync();
        Assert.True(google.RevokedAccess); Assert.Equal(stored, it.Cloud.Files.Count);
        await using (var db = it.Db()) Assert.False(await db.BackupDriveConnections.AnyAsync());

        // Audited, and nobody but Admin may read or act.
        Assert.True((int)(await it.Execute("SELECT COUNT(DISTINCT ActionCode) FROM dbo.AuditEvents WHERE ActionCode IN " +
            "('backup.drive_connect_started','backup.drive_connected','backup.full_requested','backup.settings_changed','backup.recovery_requested','backup.report_exported','backup.drive_disconnected')", it.Catalog))! == 7);
        using var owner = it.World.Client(it.World.OwnerA, null);
        foreach (var url in new[] { "overview", "points", "report", "report/export" })
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/admin/backups/" + url)).StatusCode);
        foreach (var url in new[] { "jobs/full", "jobs/log", "jobs/verify", "jobs/restore-drill", "jobs/catalog-sync", "recovery", "drive/connect", "drive/check" })
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/admin/backups/" + url, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/admin/backups/settings", new BackupSettingsDto("01:30", 4, 14, false, true, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync("/api/admin/backups/drive")).StatusCode);
        using var anonymous = it.World.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/admin/backups/jobs/full", new { })).StatusCode);
        // A forged callback state changes nothing.
        using var raw = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = api.BaseAddress };
        var forged = await raw.GetAsync("/api/admin/backups/drive/callback?code=good-code&state=forged");
        Assert.Contains("drive=failed-invalid_state", forged.Headers.Location?.ToString());
        await using (var db = it.Db()) Assert.False(await db.BackupDriveConnections.AnyAsync());
    }
}
