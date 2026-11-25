extern alias web;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
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
using SmartCv.Core.Entities;

namespace SmartCv.Tests.Auth;

public class B43BrowserTests
{
    [B43BrowserFact]
    public async Task Real_browser_uses_synthetic_API_sessions_for_files_Audit_roles_and_all_requested_widths()
    {
        var gate = new AuditGate();
        using var api = B43AuditTests.Factory().WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(gate)));
        api.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var adminPassword = A28TestSupport.Password(); var hrPassword = A28TestSupport.Password();
        var admin = await A28TestSupport.Seed(api, adminPassword); var hr = await A28TestSupport.Seed(api, hrPassword, "HR");
        using var apiHttp = A29AuthorizationTests.Client(api, admin);
        // CV files are business data: uploaded and opened by HR (Admin is refused since #114; B57RoleBoundaryTests.R1).
        using var hrApi = A29AuthorizationTests.Client(api, hr);
        var files = new List<(Guid Id, string Name, string Type)>();
        files.Add((await B43AuditTests.Upload(hrApi, Pdf(), "CV tổng hợp.pdf"), "CV tổng hợp.pdf", "application/pdf"));
        using (var bitmap = new SkiaSharp.SKBitmap(8, 8))
        {
            bitmap.Erase(SkiaSharp.SKColors.White); using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            foreach (var format in new[] { SkiaSharp.SKEncodedImageFormat.Png, SkiaSharp.SKEncodedImageFormat.Jpeg })
            {
                using var data = image.Encode(format, 90); var extension = format == SkiaSharp.SKEncodedImageFormat.Png ? "png" : "jpg";
                var name = "CV tổng hợp." + extension;
                files.Add((await B43AuditTests.Upload(hrApi, data.ToArray(), name), name, extension == "png" ? "image/png" : "image/jpeg"));
            }
        }
        var candidate = new Candidate { FullName = "Ứng viên tổng hợp B43", Email = Guid.NewGuid().ToString("N") + "@example.invalid" };
        await A28TestSupport.WithDb(api, async db =>
        {
            db.Candidates.Add(candidate); await db.SaveChangesAsync();
            foreach (var cv in await db.Cvs.ToListAsync()) cv.CandidateId = candidate.CandidateId;
            // Persisted synthetic events exercise real server paging; the UI has no fixture-specific data path.
            db.AuditEvents.AddRange(Enumerable.Range(1, 30).Select(i => new AuditEvent { OccurredAtUtc = DateTime.UtcNow.AddSeconds(-i),
                ActorUserId = admin.UserId, ActorNameSnapshot = admin.FullName, ActorRoleSnapshot = "Admin", ActionCode = "jobs.updated",
                Category = "Recruitment", ResourceType = "Job", ResourceId = i.ToString(CultureInfo.InvariantCulture), Outcome = i % 5 == 0 ? "Denied" : "Succeeded",
                RequestId = "synthetic-browser-" + i, SafeSummary = "Cập nhật vị trí tuyển dụng" }));
            await db.SaveChangesAsync();
        });
        using var webFactory = new WebApplicationFactory<web::Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development"); b.UseSetting("ApiBaseUrl", apiHttp.BaseAddress!.ToString());
            b.ConfigureTestServices(s => s.AddDataProtection().UseEphemeralDataProtectionProvider());
            b.ConfigureLogging(l => l.ClearProviders());
        });
        webFactory.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        using var webHttp = webFactory.CreateClient(); var root = webHttp.BaseAddress!.ToString();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = B43BrowserFact.Chrome() });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1366, Height = 900 }, AcceptDownloads = true });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(20000);
        var consoleErrors = new ConcurrentQueue<string>(); var unsafeUrls = new ConcurrentQueue<string>();
        page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Enqueue(message.Text); };
        page.PageError += (_, message) => consoleErrors.Enqueue(message);
        page.Request += (_, request) => { if (System.Text.RegularExpressions.Regex.IsMatch(request.Url, @"(?i)[?&](access_token|token|jwt)=")) unsafeUrls.Enqueue("Credential in URL"); };
        var artifacts = TestSupport.RepoFile("artifacts/b43/browser"); Directory.CreateDirectory(artifacts);
        var checks = new List<string>(); var stage = "login Admin";
        try
        {
            await Login(page, root, admin.Email, adminPassword);
            stage = "Audit loading"; gate.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.GotoAsync(root + "admin/activity-logs");
            await page.GetByText("Đang tải nhật ký hoạt động…", new() { Exact = true }).WaitForAsync();
            gate.Block.TrySetResult(); gate.Block = null;
            await page.Locator(".audit-list tbody tr").First.WaitForAsync(); checks.Add("Admin Audit loading/data");
            Assert.True(await page.GetByRole(AriaRole.Link, new() { Name = "Audit Log", Exact = true }).CountAsync() > 0);
            stage = "responsive";
            foreach (var width in new[] { 360, 375, 390, 768, 1024, 1366, 1920 })
            {
                await page.SetViewportSizeAsync(width, 900);
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"), "Horizontal overflow at " + width);
                if (width is 360 or 1366) await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "activity-" + width + ".png"), FullPage = true });
                checks.Add("responsive " + width);
            }
            await page.SetViewportSizeAsync(1366, 900);
            stage = "pagination/detail"; await page.GetByRole(AriaRole.Button, new() { Name = "Sau →", Exact = true }).ClickAsync();
            await page.GetByText("Trang 2/", new() { Exact = false }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "← Trước", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Xem chi tiết sự kiện", Exact = false }).First.ClickAsync();
            await page.Locator("#audit-detail-title").WaitForAsync();
            // Interactive Server can still render the detail response after the heading appears.
            // Finish that request and the close render before editing a field bound on change.
            await page.Locator(".event-detail").WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Đóng", Exact = true }).ClickAsync();
            await page.Locator("#audit-detail-title").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            checks.Add("pagination/detail");
            stage = "empty filter"; await page.Locator("form.audit-filters input[type=search]").FillAsync("no-synthetic-event-has-this-text"); await page.GetByRole(AriaRole.Button, new() { Name = "Áp dụng", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Không có sự kiện phù hợp" }).WaitForAsync(); checks.Add("empty filter");
            stage = "unavailable/retry"; gate.Unavailable = true;
            await page.GetByRole(AriaRole.Button, new() { Name = "Làm mới", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Chưa tải được nhật ký" }).WaitForAsync();
            gate.Unavailable = false; await page.GetByRole(AriaRole.Button, new() { Name = "Thử lại", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Không có sự kiện phù hợp" }).WaitForAsync(); checks.Add("unavailable/retry");
            stage = "HR roles";
            await using var hrContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1366, Height = 900 }, AcceptDownloads = true });
            var hrPage = await hrContext.NewPageAsync(); hrPage.SetDefaultTimeout(20000); await Login(hrPage, root, hr.Email, hrPassword);
            stage = "candidate files"; await hrPage.GotoAsync(root + "candidates/" + candidate.CandidateId);
            await hrPage.Locator("#candidate-name").WaitForAsync();
            // New browser has no anonymity preference; toggle only if the fail-closed UI currently has it enabled.
            var toggle = hrPage.GetByRole(AriaRole.Switch);
            if (await toggle.CountAsync() > 0 && await toggle.First.GetAttributeAsync("aria-checked") == "true") await toggle.First.ClickAsync();
            await hrPage.GetByText(candidate.FullName, new() { Exact = true }).First.WaitForAsync();
            foreach (var file in files)
            {
                stage = "preview " + file.Type;
                var card = hrPage.Locator("article.cv-card").Filter(new() { HasText = file.Name });
                await card.GetByRole(AriaRole.Button, new() { Name = "Xem file", Exact = true }).ClickAsync();
                await hrPage.Locator("dialog.sc-file-preview[open]").WaitForAsync();
                if (file.Type.StartsWith("image/", StringComparison.Ordinal)) await hrPage.WaitForFunctionAsync("document.querySelector('.sc-file-preview img')?.naturalWidth > 0");
                else Assert.StartsWith("blob:", await hrPage.Locator(".sc-file-preview iframe").GetAttributeAsync("src"), StringComparison.Ordinal);
                Assert.Single(hrContext.Pages); // no blank popup/tab
                await hrPage.Locator(".sc-file-preview").GetByRole(AriaRole.Button, new() { Name = "Đóng", Exact = true }).ClickAsync();
                var downloadTask = hrPage.WaitForDownloadAsync(); await card.GetByRole(AriaRole.Button, new() { Name = "Tải file CV về máy", Exact = true }).ClickAsync();
                var download = await downloadTask; Assert.Equal(file.Name, download.SuggestedFilename); checks.Add("preview/download " + file.Type);
            }
            stage = "export JSON"; var exportedTask = hrPage.WaitForDownloadAsync(); await hrPage.Locator("#rights-export").ClickAsync();
            var exported = await exportedTask; Assert.EndsWith(".json", exported.SuggestedFilename);
            Assert.Contains(candidate.FullName, await File.ReadAllTextAsync((await exported.PathAsync())!)); checks.Add("JSON export");
            Assert.Equal(0, await hrPage.Locator("#rights-actor,input#hr-actor,input#merge-actor,input#erase-handled-by").CountAsync());
            Assert.True(await hrPage.EvaluateAsync<bool>("!Object.values(sessionStorage).some(v=>v.startsWith('eyJ')) && !Object.keys(localStorage).some(k=>/token|jwt|auth/i.test(k))"));
            checks.Add("no actor inputs or plaintext JWT/storage/URL");
            await hrPage.GotoAsync(root + "runs"); await hrPage.Locator("h1").WaitForAsync(); Assert.DoesNotContain("403", await hrPage.Locator("h1").InnerTextAsync());
            Assert.Equal(0, await hrPage.GetByRole(AriaRole.Link, new() { Name = "Audit Log", Exact = true }).CountAsync());
            // B28 AccessDenied renders "Mã 403 · Không đủ quyền" + #denied-title; the audit table must never render for HR.
            await hrPage.GotoAsync(root + "admin/activity-logs"); await hrPage.Locator("#denied-title").WaitForAsync();
            Assert.Contains("403", await hrPage.Locator(".denied-code").InnerTextAsync());
            Assert.Equal(0, await hrPage.Locator(".audit-list, .audit-filters").CountAsync()); checks.Add("HR /runs allowed; Audit menu hidden and direct route forbidden");
            Assert.Empty(unsafeUrls); Assert.True(consoleErrors.IsEmpty, "Browser emitted errors; inspect synthetic browser environment.");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "results.json"), JsonSerializer.Serialize(new { Status = "PASS", Checks = checks, ConsoleErrors = consoleErrors.Count, UnsafeUrls = unsafeUrls.Count }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            gate.Block?.TrySetResult();
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "failure.png"), FullPage = true });
            await File.WriteAllTextAsync(Path.Combine(artifacts, "failure.json"), JsonSerializer.Serialize(new { Stage = stage, Type = error.GetType().Name, Checks = checks, ConsoleErrors = consoleErrors.Count }));
            // Playwright exception messages may contain FillAsync arguments. Never copy credentials into test logs.
            throw new Xunit.Sdk.XunitException("B43 browser failed at " + stage + " (" + error.GetType().Name + "). See ignored synthetic screenshots/step log.");
        }
        finally { gate.Block?.TrySetResult(); }
    }

    private static async Task Login(IPage page, string root, string email, string password)
    {
        await page.GotoAsync(root + "login"); await page.Locator("#login-email").FillAsync(email); await page.Locator("#login-password").FillAsync(password);
        await page.Locator("button[type=submit]").ClickAsync(); await page.WaitForURLAsync(url => !new Uri(url).AbsolutePath.StartsWith("/login", StringComparison.Ordinal));
    }
    private static byte[] Pdf()
    {
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        const string content = "BT /F1 12 Tf 20 350 Td (Synthetic B43 CV) Tj ET";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}\nendstream" };
        for (var i = 0; i < objects.Length; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = text.Length; text.Append("xref\n0 6\n0000000000 65535 f \n"); foreach (var offset in offsets.Skip(1)) text.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(text.ToString());
    }
    private sealed class AuditGate : IStartupFilter
    {
        internal TaskCompletionSource? Block; internal volatile bool Unavailable;
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (http, continuation) =>
            {
                if (http.Request.Path.StartsWithSegments("/api/admin/audit-events"))
                {
                    if (Block is { } blocked) await blocked.Task.WaitAsync(http.RequestAborted);
                    if (Unavailable) { http.Response.StatusCode = 503; await http.Response.WriteAsJsonAsync(new { title = "Synthetic audit unavailable", code = "audit_unavailable" }); return; }
                }
                await continuation();
            });
            next(app);
        };
    }
}

public sealed class B43BrowserFact : FactAttribute
{
    public B43BrowserFact() { if (Chrome() is null) Skip = "NOT VERIFIED: install Chromium for the synthetic B43 browser test."; }
    internal static string? Chrome()
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
        return Directory.Exists(cache) ? Directory.EnumerateDirectories(cache, "chromium-*").OrderDescending()
            .SelectMany(d => Directory.EnumerateFiles(d, "chrome.exe", SearchOption.AllDirectories)).FirstOrDefault() : null;
    }
}
