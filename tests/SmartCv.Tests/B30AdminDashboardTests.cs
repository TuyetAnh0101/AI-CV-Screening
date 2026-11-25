extern alias web;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Contracts;
using SmartCv.Core.Ops;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

public class B30AdminDashboardTests
{
    [Fact]
    public async Task Dashboard_uses_bounded_existing_APIs_and_distinguishes_counts_and_dependency_failure()
    {
        var api = new B30Api(); using var services = B27.WebServices(api.Handler);
        using var scope = services.CreateScope(); var sp = scope.ServiceProvider;
        await sp.GetRequiredService<AuthSession>().SignInAsync(B27.Login("Admin"));
        using var workspace = Workspace(sp); await workspace.LoadAsync();
        Assert.Equal(new AdminAccountCounts(35, 3, 32), workspace.Accounts.Data);
        Assert.Equal(120, workspace.Recruitment.Data!.TotalCvs);
        Assert.Equal(180, workspace.Recruitment.Data.TotalApplications);
        Assert.Equal(41, workspace.Screening.Data!.TotalCount);
        Assert.Equal(2, workspace.FailedScreening.Data!.TotalCount);
        Assert.Equal("error", workspace.Health.Data!.Status); Assert.Null(workspace.Health.Error);
        Assert.Equal(workspace.To.AddDays(-6), workspace.From);
        Assert.All(api.Handler.Calls, c => Assert.Equal("GET", c.Method));
        Assert.All(api.Handler.Calls.Where(c => !c.PathAndQuery.StartsWith("/api/health")), c => Assert.Equal("Bearer Admin", c.Authorization));
        Assert.Null(Assert.Single(api.Handler.Calls, c => c.PathAndQuery == "/api/health").Authorization);
        Assert.Equal(3, api.Handler.Calls.Count(c => c.PathAndQuery.StartsWith("/api/users?") && c.PathAndQuery.Contains("pageSize=1")));
        Assert.Equal(2, api.Handler.Calls.Count(c => c.PathAndQuery.StartsWith("/api/runs?type=Screening&page=1&pageSize=1")));
        Assert.Single(api.Handler.Calls, c => c.PathAndQuery.StartsWith("/api/admin/audit-events?") && c.PathAndQuery.EndsWith("page=1&pageSize=5"));
        await workspace.ExportPageAsync(2);
        Assert.Equal(2, workspace.Exports.Data!.Page); Assert.Single(workspace.Exports.Data.Items);
        var calls = api.Handler.Calls.Count; await workspace.ExportPageAsync(3); Assert.Equal(calls, api.Handler.Calls.Count);
    }

    [Fact]
    public async Task A_partial_outage_or_missing_header_never_becomes_a_fake_zero_and_refresh_recovers()
    {
        var api = new B30Api { MissingAccountTotal = true, SummaryUnavailable = true };
        using var services = B27.WebServices(api.Handler); using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(B27.Login("Admin"));
        using var workspace = Workspace(scope.ServiceProvider); await workspace.LoadAsync();
        Assert.Null(workspace.Accounts.Data); Assert.NotNull(workspace.Accounts.Error);
        Assert.Null(workspace.Recruitment.Data); Assert.NotNull(workspace.Recruitment.Error);
        Assert.NotNull(workspace.Activity.Data); Assert.NotNull(workspace.Exports.Data);
        api.MissingAccountTotal = false; api.SummaryUnavailable = false; api.EmptyJournals = true;
        await workspace.LoadAsync();
        Assert.Null(workspace.Recruitment.Error); Assert.Equal(35, workspace.Accounts.Data!.Total);
        Assert.Empty(workspace.Exports.Data!.Items); Assert.Empty(workspace.Activity.Data!.Items);
    }

    [Theory]
    [InlineData("Admin", false, true)]
    [InlineData("HR", false, false)]
    [InlineData("Candidate", false, false)]
    [InlineData("Admin", true, false)]
    [InlineData(null, false, false)]
    public async Task Component_checks_verified_Admin_before_fetching_any_admin_data(string? role, bool mcp, bool allowed)
    {
        var api = new B30Api(); using var services = B27.WebServices(api.Handler); using var scope = services.CreateScope();
        var sp = scope.ServiceProvider; var session = sp.GetRequiredService<AuthSession>();
        if (role is not null) await session.SignInAsync(B27.Login(role, role, mcp));
        else await session.EnsureRestoredAsync();
        var auth = await sp.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
        await using var renderer = new HtmlRenderer(sp, sp.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment child = b => { b.OpenComponent<AdminDashboard>(0); b.CloseComponent(); };
            var root = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = Task.FromResult(auth), ["ChildContent"] = child }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Equal(allowed, html.Contains("Quản trị &amp; vận hành") || html.Contains("Quản trị & vận hành"));
        if (!allowed) Assert.Empty(api.Handler.Calls);
        else { Assert.Contains("Chưa xác minh", html); Assert.Contains("API đã phản hồi", html); Assert.Contains("120", html); Assert.Contains("180", html); }
    }

    [Theory]
    [InlineData(401, "invalid_token")]
    [InlineData(403, "forbidden")]
    [InlineData(403, "password_change_required")]
    public async Task New_export_reader_preserves_existing_auth_failure_handling(int status, string code)
    {
        var api = new B27CaptureHandler { Respond = _ => Task.FromResult(B27CaptureHandler.Problem((HttpStatusCode)status, code)) };
        using var services = B27.WebServices(api); using var scope = services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<AuthSession>(); await session.SignInAsync(B27.Login("Admin"));
        var result = await scope.ServiceProvider.GetRequiredService<ExportApiClient>().ListAsync(new(2026, 11, 14), new(2026, 11, 20));
        Assert.False(result.IsSuccess); Assert.Equal(status, result.StatusCode);
        Assert.Equal("Bearer Admin", Assert.Single(api.Calls).Authorization);
        if (status == 401) Assert.False(session.IsSignedIn);
        else if (code == "password_change_required") Assert.True(session.User!.MustChangePassword);
    }

    [Fact]
    public async Task Health_rejects_bad_or_inconsistent_reports_instead_of_claiming_ready()
    {
        foreach (var body in new object[] { new { status = "ok" }, new HealthReportDto("ok", DateTime.UtcNow, [new("database", true, "ok"), new("uploads", true, "ok"), new("ocr", false, "missing"), new("email", true, "ok")]) }) // all 4 checks: rejected only for "ok" vs failed OCR
        {
            var handler = new B27CaptureHandler { Respond = _ => Task.FromResult(B27CaptureHandler.Json(HttpStatusCode.OK, body)) };
            using var services = B27.WebServices(handler); using var scope = services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<HealthApiClient>().GetReadinessAsync();
            Assert.False(result.IsSuccess); Assert.Equal(ApiFailureKind.InvalidData, result.FailureKind);
        }
    }

    internal static AdminDashboardWorkspace Workspace(IServiceProvider sp) => new(sp.GetRequiredService<DashboardApiClient>(), sp.GetRequiredService<UserApiClient>(), sp.GetRequiredService<RunApiClient>(), sp.GetRequiredService<HealthApiClient>(), sp.GetRequiredService<AuditApiClient>(), sp.GetRequiredService<ExportApiClient>(), TimeProvider.System);

    [Fact]
    public async Task Duplicate_refresh_is_ignored_and_disposal_prevents_late_data_updates()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new B30Api { HoldSummary = gate };
        using var services = B27.WebServices(api.Handler); using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AuthSession>().SignInAsync(B27.Login("Admin"));
        var workspace = Workspace(scope.ServiceProvider);
        var first = workspace.LoadAsync(); await workspace.LoadAsync();
        Assert.Single(api.Handler.Calls, c => c.PathAndQuery == "/api/dashboard/summary");
        workspace.Dispose(); gate.TrySetResult(); await first;
        Assert.Null(workspace.Recruitment.Data); Assert.Null(workspace.CheckedAt);
    }
}

// Synthetic HTTP responses only. No API host, seed, SQL connection or real account is used by B30 tests.
internal sealed class B30Api
{
    internal readonly B27CaptureHandler Handler;
    internal volatile bool MissingAccountTotal, SummaryUnavailable, EmptyJournals, OverviewUnavailable;
    internal TaskCompletionSource? HoldSummary;
    internal B30Api() => Handler = new() { Respond = Respond };
    private async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var q = QueryHelpers.ParseQuery(request.RequestUri.Query);
        object body;
        if (path == "/api/auth/login")
        {
            var text = await request.Content!.ReadAsStringAsync();
            var role = text.Contains("candidate@") ? "Candidate" : text.Contains("hr@") ? "HR" : "Admin";
            return B27CaptureHandler.Json(HttpStatusCode.OK, B27.Login(text.Contains("mcp@") ? "mcp" : role, role, text.Contains("mcp@")));
        }
        if (path == "/api/auth/me")
        {
            var token = request.Headers.Authorization?.Parameter;
            return B27CaptureHandler.Json(HttpStatusCode.OK, B27.User(token == "HR" ? "HR" : token == "Candidate" ? "Candidate" : "Admin", token == "mcp"));
        }
        if (path == "/api/health") return B27CaptureHandler.Json(HttpStatusCode.ServiceUnavailable,
            new HealthReportDto("error", DateTime.UtcNow, [new("database", true, "Kết nối và schema hợp lệ."), new("uploads", true, "Kho lưu trữ sẵn sàng."), new("ocr", false, "Thiếu dữ liệu ngôn ngữ OCR."), new("email", true, "Email ở chế độ File.")])); // HealthApiClient requires all 4 checks
        if (path == "/api/dashboard/summary")
        {
            if (HoldSummary is { } gate) await gate.Task;
            if (SummaryUnavailable) return B27CaptureHandler.Problem(HttpStatusCode.ServiceUnavailable, "summary_unavailable");
            body = new DashboardSummaryDto { TotalCvs = 120, TotalApplications = 180, ActiveJobs = 7, RunningRuns = 3, FailedCvs = 4, FlaggedUnresolved = 9, FlaggedJobId = 3, RejectedCount = 18, Funnel = [new() { Stage = "Shortlisted", Current = 25 }, new() { Stage = "Interview", Current = 12 }] };
        }
        else if (path == "/api/admin/ops/overview")
        {
            // #116/#120 admin operations dashboard: aggregate counts only.
            if (OverviewUnavailable) return B27CaptureHandler.Problem(HttpStatusCode.ServiceUnavailable, "admin_ops_unavailable");
            body = new AdminSystemOverviewDto(DateTime.UtcNow, 35, 33, 6, 4, 9, 7, 120, 180, 41, 3, 2, 17, 5);
        }
        else if (path == "/api/admin/ops/ai-history") return B27CaptureHandler.Problem(HttpStatusCode.ServiceUnavailable, "ai_history_unavailable");
        else if (path == "/api/dashboard/trend") body = new DashboardTrendDto { From = new(2026, 11, 14), To = new(2026, 11, 20) };
        else if (path == "/api/users")
        {
            var response = B27CaptureHandler.Json(HttpStatusCode.OK, Array.Empty<UserDto>());
            if (!MissingAccountTotal) response.Headers.Add("X-Total-Count", q.GetValueOrDefault("role") == "Admin" ? "3" : q.GetValueOrDefault("role") == "HR" ? "32" : "35");
            return response;
        }
        else if (path == "/api/runs") body = new RunPageDto { Page = 1, PageSize = 1, TotalCount = q.GetValueOrDefault("status") == "Failed" ? 2 : 41, TotalPages = 41 };
        else if (path == "/api/admin/audit-events") body = new AuditPageDto { Page = 1, PageSize = 5, TotalCount = EmptyJournals ? 0 : 24, FailureCount = EmptyJournals ? 0 : 2, Items = EmptyJournals ? [] : [new(701, DateTime.UtcNow, 7, "Người kiểm thử", "Admin", "jobs.updated", "Recruitment", "Job", "3", "Succeeded", "synthetic-b30", "Cập nhật vị trí kỹ sư phần mềm", null)] };
        else if (path == "/api/exports")
        {
            var page = int.TryParse(q["page"], out var number) ? number : 1;
            body = new ExportLogPageDto(page, 5, EmptyJournals ? 0 : 6, EmptyJournals ? 0 : 2, EmptyJournals ? [] : [new(page == 1 ? 901 : 902, 701, 11, 3, page == 1 ? "Kỹ sư phần mềm · Tuyển dụng tháng 11" : "Chuyên viên kiểm thử · Trang thứ hai", "xlsx", "All", "All", false, false, false, 20, true, 7, "Người kiểm thử", DateTime.UtcNow)]);
        }
        else if (path == "/api/jobs") body = new[] { new JobDto { JobId = 3, Title = "Kỹ sư phần mềm", IsActive = true } };
        else if (path == "/api/prompts/active") body = new PromptVersionDto();
        else if (path is "/api/prompts" or "/api/cvs" or "/api/candidates" or "/api/evaluation-sets") body = Array.Empty<object>();
        else return B27CaptureHandler.Problem(HttpStatusCode.NotFound, "synthetic_route_missing");
        return B27CaptureHandler.Json(HttpStatusCode.OK, body);
    }
}
