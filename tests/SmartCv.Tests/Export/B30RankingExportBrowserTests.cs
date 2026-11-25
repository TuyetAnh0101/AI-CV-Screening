extern alias web;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Export;
using SmartCv.Infrastructure.Data;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Export;

public class B30RankingExportBrowserTests
{
    [B43BrowserFact]
    public async Task Excel_flows_use_real_A31_in_isolated_memory_and_stream_to_the_browser_for_HR_and_Admin()
    {
        var gate = new ExportGate();
        using var api = A28TestSupport.Factory(new() { ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false" })
            .WithWebHostBuilder(b => { b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(gate)); b.ConfigureLogging(l => l.ClearProviders()); });
        api.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var adminPassword = A28TestSupport.Password(); var hrPassword = A28TestSupport.Password();
        // Since #114 ranking export is HR business work (Admin is refused, B57RoleBoundaryTests.R1): the desktop
        // export journey runs with a second internal HR account; "admin" keeps its variable name only.
        var admin = await A28TestSupport.Seed(api, adminPassword, "HR"); var hr = await A28TestSupport.Seed(api, hrPassword, "HR");
        var run = await A31ExportTests.Seed(api); var running = await A31ExportTests.Seed(api, "Running");
        using var apiHttp = A29AuthorizationTests.Client(api, admin);
        using (var scope = api.Services.CreateScope()) Assert.Equal("Microsoft.EntityFrameworkCore.InMemory", scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().Database.ProviderName);
        var ranking = (await apiHttp.GetFromJsonAsync<RankingResponse>($"api/runs/{run}/ranking"))!;
        var flagged = ranking.Items.First(r => r.Stage == "New" && r.Status == "Completed");
        using (var flag = await apiHttp.PostAsJsonAsync($"api/applications/{flagged.ApplicationId}/flags", new CreateManualFlagRequest { Reason = ManualFlagReasons.ScoreDoubt })) Assert.True(flag.IsSuccessStatusCode);
        ranking = (await apiHttp.GetFromJsonAsync<RankingResponse>($"api/runs/{run}/ranking"))!;
        using var factory = new WebApplicationFactory<web::Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development"); b.UseSetting("ApiBaseUrl", apiHttp.BaseAddress!.ToString());
            b.ConfigureTestServices(s => s.AddDataProtection().UseEphemeralDataProtectionProvider()); b.ConfigureLogging(l => l.ClearProviders());
        });
        factory.UseKestrel(o => o.Listen(IPAddress.Loopback, 0)); using var webHttp = factory.CreateClient(); var root = webHttp.BaseAddress!.ToString();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = B43BrowserFact.Chrome() });
        var artifacts = TestSupport.RepoFile("artifacts/b30-export-ui"); Directory.CreateDirectory(artifacts);
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1366, Height = 768 }, AcceptDownloads = true });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(20000);
        var errors = new ConcurrentQueue<string>(); page.PageError += (_, message) => errors.Enqueue(message);
        var stage = "desktop HR login";
        try
        {
            await Login(page, root, admin.Email, adminPassword);
            await page.GotoAsync(root + $"results?runId={run}&jobId={ranking.JobId}");
            await page.Locator("#ranking-export-open").WaitForAsync();
            stage = "Run/Job selectors";
            Assert.Equal(run.ToString(), await page.GetByRole(AriaRole.Combobox, new() { Name = "Lượt chấm" }).InputValueAsync());
            Assert.Equal(ranking.JobId.ToString(), await page.GetByRole(AriaRole.Combobox, new() { Name = "Vị trí tuyển dụng" }).InputValueAsync());
            stage = "default preview and responsive"; await Open(page, "Shortlisted", 2);
            Assert.True(await page.Locator("#export-anonymous").IsCheckedAsync());
            Assert.False(await page.Locator("input[value=pdf]").IsDisabledAsync()); // PDF ranking export enabled by #118 (A31 settings: PdfEnabled)
            foreach (var width in new[] { 360, 390, 768, 1366 })
            {
                await page.SetViewportSizeAsync(width, width < 768 ? 844 : 768);
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"), "Page overflow at " + width);
                Assert.True(await page.Locator("dialog[open]").EvaluateAsync<bool>("d => d.scrollWidth <= d.clientWidth && d.getBoundingClientRect().width <= innerWidth"), "Modal overflow at " + width);
                if (width is 390 or 1366) await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "export-" + width + ".png"), FullPage = true });
            }
            await page.SetViewportSizeAsync(1366, 900);
            await page.Keyboard.PressAsync("Tab");
            Assert.True(await page.EvaluateAsync<bool>("document.querySelector('dialog[open]').contains(document.activeElement)"));
            await page.Keyboard.PressAsync("Escape"); await page.Locator("#ranking-export-title").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await page.WaitForFunctionAsync("document.activeElement?.id === 'ranking-export-open'");
            await Open(page, "Shortlisted", 2);
            stage = "shortlist stream and duplicate click";
            gate.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); var before = gate.Downloads.Count;
            var downloadTask = page.WaitForDownloadAsync(); await page.Locator("#export-download").ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Đang tải…", Exact = true }).WaitForAsync();
            Assert.True(await page.Locator("#export-download").IsDisabledAsync());
            await page.Locator("#export-download").EvaluateAsync("b => b.click()");
            gate.Hold.TrySetResult(); gate.Hold = null;
            var first = await downloadTask;
            await Verify(first, ranking, new(ranking.JobId, "xlsx", "Shortlisted", "all", null, true, false, false), artifacts, "admin-shortlisted.xlsx");
            await page.GetByText("Đã chuyển file", new() { Exact = false }).WaitForAsync();
            Assert.True(await page.Locator("#export-download").IsDisabledAsync()); Assert.Equal(before + 1, gate.Downloads.Count);
            await Close(page);
            stage = "all scope"; await Open(page, "Shortlisted", 2); await page.Locator("#export-scope").SelectOptionAsync("All"); await Preview(page, "All", 4);
            await DownloadAndVerify(page, ranking, new(ranking.JobId, "xlsx", "all", "all", null, true, false, false), artifacts, "admin-all.xlsx"); await Close(page);
            stage = "explicit nonanonymous export";
            await Open(page, "Shortlisted", 2); await page.Locator("#export-anonymous").UncheckAsync();
            await page.WaitForFunctionAsync("document.querySelector('.export-preview')?.dataset.anonymous === 'false' && !document.getElementById('export-download').disabled");
            await DownloadAndVerify(page, ranking, new(ranking.JobId, "xlsx", "Shortlisted", "all", null, false, false, false), artifacts, "admin-explicit-names.xlsx"); await Close(page);

            stage = "matching current filters";
            var privacy = page.GetByRole(AriaRole.Switch, new() { Name = "Ẩn danh", Exact = true });
            await privacy.CheckAsync();
            await MoreFilters(page);
            await page.Locator("#stage-filter-New").ClickAsync();
            await page.GetByRole(AriaRole.Group, new() { Name = "Lọc nhanh", Exact = true }).GetByRole(AriaRole.Button, new() { Name = "Dự đoán Đạt", Exact = false }).ClickAsync();
            await page.Locator("#flag-filter").ClickAsync(); await page.Locator("#manual-filter").ClickAsync();
            await page.Locator(".ranking input[type=search]").FillAsync("HS" + flagged.ApplicationId);
            await page.GetByLabel("Sắp xếp bảng xếp hạng", new() { Exact = true }).SelectOptionAsync("AiDesc");
            await Open(page, "Shortlisted", 2); await page.Locator("#export-scope").SelectOptionAsync("Current"); await Preview(page, "Current", 1);
            await page.Locator("#export-anonymous").UncheckAsync();
            await page.GetByText("chế độ ẩn danh của file phải khớp", new() { Exact = false }).WaitForAsync();
            Assert.True(await page.Locator("#export-download").IsDisabledAsync());
            await page.Locator("#export-anonymous").CheckAsync(); await Preview(page, "Current", 1);
            var query = new RankingExportQuery(ranking.JobId, "xlsx", "New", "pass", "HS" + flagged.ApplicationId, true, true, true);
            await DownloadAndVerify(page, ranking, query, artifacts, "admin-current-filters.xlsx");
            Assert.Contains("needsReview=true", gate.Downloads.Last()); Assert.Contains("manualFlags=true", gate.Downloads.Last()); Assert.Contains("fit=pass", gate.Downloads.Last()); await Close(page);

            stage = "empty scope"; await MoreFilters(page); await page.Locator("#stage-filter-Interview").ClickAsync(); await Open(page, "Shortlisted", 2);
            await page.Locator("#export-scope").SelectOptionAsync("Current"); await Preview(page, "Current", 0, false);
            await page.GetByText("Không có hồ sơ trong phạm vi đã chọn.", new() { Exact = false }).WaitForAsync(); await Close(page);

            stage = "incomplete run"; await page.GotoAsync(root + $"results?runId={running}"); await page.Locator("#ranking-export-open").WaitForAsync();
            Assert.True(await page.Locator("#ranking-export-open").IsDisabledAsync());
            await page.GotoAsync(root + $"results?runId={run}&jobId={ranking.JobId}"); await page.Locator("#ranking-export-open").WaitForAsync();

            stage = "concurrent stage change"; await Open(page, "Shortlisted", 2);
            var moved = ranking.Items.First(r => r.Stage == "Shortlisted");
            await A28TestSupport.WithDb(api, async db => { var application = await db.Applications.SingleAsync(a => a.ApplicationId == moved.ApplicationId); application.Stage = "Interview"; await db.SaveChangesAsync(); });
            before = gate.Downloads.Count; await page.Locator("#export-download").ClickAsync();
            await page.GetByText("Dữ liệu vừa thay đổi.", new() { Exact = false }).WaitForAsync(); Assert.Equal(before, gate.Downloads.Count); await Close(page);
            await A28TestSupport.WithDb(api, async db => { var application = await db.Applications.SingleAsync(a => a.ApplicationId == moved.ApplicationId); application.Stage = "Shortlisted"; await db.SaveChangesAsync(); });

            foreach (var status in new[] { 403, 404, 409, 503 })
            {
                stage = "file failure " + status; gate.Failure = status; await Open(page, "Shortlisted", 2); await page.Locator("#export-download").ClickAsync();
                await page.Locator(".export-error").WaitForAsync();
                var text = await page.Locator(".export-error").InnerTextAsync();
                Assert.Contains(status switch { 403 => "không có quyền", 404 => "Không tìm thấy", 409 => "chưa đủ điều kiện", _ => "tạm không khả dụng" }, text);
                Assert.Contains("/results", page.Url); Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Đăng xuất", Exact = true }).IsVisibleAsync()); await Close(page);
            }
            gate.Failure = 0;
            stage = "HR end-to-end and expired session";
            await using var hrContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 }, AcceptDownloads = true });
            var hrPage = await hrContext.NewPageAsync(); hrPage.SetDefaultTimeout(20000); hrPage.PageError += (_, message) => errors.Enqueue(message);
            await Login(hrPage, root, hr.Email, hrPassword); await hrPage.GotoAsync(root + $"results?runId={run}&jobId={ranking.JobId}");
            await Open(hrPage, "Shortlisted", 2); await DownloadAndVerify(hrPage, ranking, new(ranking.JobId, "xlsx", "Shortlisted", "all", null, true, false, false), artifacts, "hr-shortlisted.xlsx"); await Close(hrPage);
            gate.Failure = 401; await Open(hrPage, "Shortlisted", 2); await hrPage.Locator("#export-download").ClickAsync();
            await hrPage.WaitForURLAsync(root + "login?*");
            Assert.DoesNotContain(gate.Requests, path => path.Contains("format=pdf")); Assert.Empty(errors);
        }
        catch (Exception error)
        {
            gate.Hold?.TrySetResult(); await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "failure.png"), FullPage = true });
            throw new InvalidOperationException("Synthetic B30 browser stage: " + stage, error);
        }
    }

    private static async Task Login(IPage page, string root, string email, string password)
    {
        await page.GotoAsync(root + "login"); await page.Locator("#login-email").FillAsync(email); await page.Locator("#login-password").FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Đăng nhập", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath != "/login");
    }
    /// <summary>#112 moved the HR stage / flag filters into the collapsible "Giai đoạn HR và cờ" panel; open it when closed.</summary>
    private static async Task MoreFilters(IPage page)
    {
        var panel = page.Locator("details.ranking-more-filters");
        if (!await panel.EvaluateAsync<bool>("d => d.open")) await panel.Locator("summary").ClickAsync();
        await page.Locator("#stage-filter-New").WaitForAsync();
    }
    private static async Task Open(IPage page, string scope, int count)
    {
        await page.Locator("#ranking-export-open").ClickAsync(); await page.Locator("dialog[open]").WaitForAsync(); await Preview(page, scope, count);
    }
    private static Task Preview(IPage page, string scope, int count, bool allowed = true) => page.WaitForFunctionAsync("v => { const p=document.querySelector('.export-preview'); return p?.dataset.scope===v.scope && p.getAttribute('aria-busy')==='false' && p.querySelector('.preview-count>strong')?.textContent===String(v.count) && document.getElementById('export-download').disabled===!v.allowed; }", new { scope, count, allowed });
    private static async Task Close(IPage page)
    {
        await page.Locator("dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Đóng", Exact = true }).Or(page.Locator("dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Hủy", Exact = true })).ClickAsync();
        await page.Locator("#ranking-export-title").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }
    private static async Task DownloadAndVerify(IPage page, RankingResponse ranking, RankingExportQuery query, string artifacts, string name)
    {
        var download = page.WaitForDownloadAsync(); await page.Locator("#export-download").ClickAsync();
        await Verify(await download, ranking, query, artifacts, name); await page.GetByText("Đã chuyển file", new() { Exact = false }).WaitForAsync();
    }
    private static async Task Verify(IDownload download, RankingResponse ranking, RankingExportQuery query, string artifacts, string name)
    {
        Assert.Matches($"^xep-hang-luot-{ranking.RunId}-vi-tri-{ranking.JobId}-[0-9]{{8}}-[0-9]{{6}}\\.xlsx$", download.SuggestedFilename);
        var path = Path.Combine(artifacts, name); await download.SaveAsAsync(path);
        using var book = new XLWorkbook(path); var sheet = book.Worksheet(1); var expected = query.Apply(ranking.Items);
        Assert.Equal(7 + expected.Count, sheet.LastRowUsed()!.RowNumber()); Assert.Contains(ranking.JobTitle, sheet.Cell(1, 1).GetString()); Assert.Contains("#" + ranking.RunId, sheet.Cell(1, 1).GetString());
        for (var i = 0; i < expected.Count; i++)
        {
            var row = expected[i]; Assert.Equal(query.Anonymous ? RankingExportQuery.Alias(row.Rank, row.ApplicationId) : row.CandidateName, sheet.Cell(8 + i, 2).GetString());
            if (row.Rank is { } rank) Assert.Equal(rank, sheet.Cell(8 + i, 1).GetValue<int>());
        }
        if (query.Anonymous) Assert.DoesNotContain("Nguyễn Văn An", string.Join('|', sheet.CellsUsed().Select(c => c.GetString())));
    }
    private sealed class ExportGate : IStartupFilter
    {
        internal readonly ConcurrentQueue<string> Requests = new(), Downloads = new();
        internal volatile int Failure; internal TaskCompletionSource? Hold;
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (http, continuation) =>
            {
                if (http.Request.Path.Value?.Contains("/ranking/export", StringComparison.Ordinal) == true)
                {
                    Requests.Enqueue(http.Request.Path + http.Request.QueryString);
                    if (!http.Request.Path.Value.EndsWith("/preview", StringComparison.Ordinal))
                    {
                        Downloads.Enqueue(http.Request.Path + http.Request.QueryString);
                        if (Hold is { } blocked) await blocked.Task.WaitAsync(http.RequestAborted);
                        if (Failure != 0) { http.Response.StatusCode = Failure; await http.Response.WriteAsJsonAsync(new { title = "Synthetic export error", code = "synthetic_failure" }); return; }
                    }
                }
                await continuation();
            });
            next(app);
        };
    }
}
