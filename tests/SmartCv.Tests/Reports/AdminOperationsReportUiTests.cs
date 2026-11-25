extern alias web;
using System.Reflection;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests.Reports;

public sealed class AdminOperationsReportUiTests
{
    private static ClaimsPrincipal User(string role, string? kind = null, string? workspaceRole = null)
    {
        var claims = new List<Claim> { new("role", role), new(AccessRules.SessionClaim, AccessRules.Verified) };
        if (kind is not null) claims.Add(new(AccessRules.WorkspaceKindClaim, kind));
        if (workspaceRole is not null) claims.Add(new(AccessRules.WorkspaceRoleClaim, workspaceRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AccessRules.AuthenticationType, "name", "role"));
    }

    [Fact]
    public void Admin_report_route_is_explicit_and_protected_by_admin_policy()
    {
        var type = typeof(AdminOperationsReport);
        Assert.Contains(type.GetCustomAttributes<RouteAttribute>(), a => a.Template == "/admin/reports");
        Assert.Contains(type.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == AccessRules.AdminPolicy);
        Assert.False(AccessRules.IsAdmin(User("HR", "Personal", "Owner")));
        Assert.False(AccessRules.IsAdmin(User("HR", "Internal", "Member")));
        Assert.True(AccessRules.IsAdmin(User("Admin")));
    }

    [Fact]
    public void Admin_sidebar_has_report_and_exactly_one_account_management_entry()
    {
        var admin = RoleBasedNavigation.For(User("Admin")).SelectMany(g => g.Items).ToArray();
        Assert.Contains(admin, i => i.Href == "admin/reports");
        Assert.Single(admin, i => i.Href == "users");   // "Quản lý tài khoản" restored, never duplicated
        Assert.DoesNotContain(admin, i => i.Href is "account" or "reports/recruitment");
        foreach (var role in new[] { User("HR", "Internal", "Member"), User("HR", "Personal", "Owner") })
            Assert.DoesNotContain(RoleBasedNavigation.For(role).SelectMany(g => g.Items), i => i.Href == "admin/reports");
    }

    [Fact]
    public void Client_builds_encoded_and_bounded_date_query_without_identity()
    {
        var from = new DateOnly(2026, 11, 10);
        var to = new DateOnly(2026, 11, 25);
        var q = AdminOperationsReportApiClient.Period(from, to);
        Assert.Equal("?from=2026-11-10&to=2026-11-25", q);
        Assert.DoesNotContain("token", q, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspaceId", q, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", q, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Contracts_explicitly_expose_absence_of_quota_and_backup_metrics()
    {
        var ai = new AdminReportAiStatsDto(0, 0, 0, 0, null, "not_instrumented", 0, 0,
            false, null, null, null, null, "not_available");
        Assert.Null(ai.QuotaExceededEvents);
        Assert.Null(ai.GeminiAttempts);
        var backup = new AdminReportBackupStatsDto("not_available_until_A62", null, "not_available");
        Assert.Null(backup.LastCompletedAtUtc);
    }
    [Fact]
    public async Task Client_downloads_csv_with_circuit_bearer_and_exact_date_scope()
    {
        var from = new DateOnly(2026, 11, 10);
        var to = new DateOnly(2026, 11, 25);
        var period = new AdminReportPeriodDto(from, to, "Asia/Ho_Chi_Minh (UTC+07:00)", DateTime.UtcNow);
        var handler = new B27CaptureHandler
        {
            Respond = request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/preview")
                ? B27CaptureHandler.Json(HttpStatusCode.OK,
                    new AdminReportExportPreviewDto(period, 4, 1000, true, null, "csv"))
                : CsvFile())
        };
        using var services = B27.WebServices(handler);
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AuthSession>()
            .SignInAsync(B27.Login("admin-jwt", "Admin"));
        var client = scope.ServiceProvider.GetRequiredService<AdminOperationsReportApiClient>();
        var preview = await client.PreviewAsync(from, to);
        Assert.True(preview.IsSuccess);
        Assert.Equal(4, preview.Data!.ExportEventCount);
        var downloaded = await client.DownloadCsvAsync(from, to);
        Assert.True(downloaded.IsSuccess);
        await using var file = downloaded.Data!;
        Assert.Equal("text/csv", file.ContentType);
        Assert.EndsWith(".csv", file.FileName);
        using var bytes = new MemoryStream();
        await file.Content.CopyToAsync(bytes);
        Assert.Equal("section,metric\n", Encoding.UTF8.GetString(bytes.ToArray()));
        var calls = handler.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, c => Assert.Equal("Bearer admin-jwt", c.Authorization));
        Assert.Contains("/api/admin/reports/preview?from=2026-11-10&to=2026-11-25", calls[0].PathAndQuery);
        Assert.Contains("/api/admin/reports/download?from=2026-11-10&to=2026-11-25&format=csv", calls[1].PathAndQuery);
    }

    private static HttpResponseMessage CsvFile()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("section,metric\n")) };
        response.Content.Headers.ContentType = new("text/csv");
        response.Content.Headers.ContentDisposition = new("attachment") { FileNameStar = "scapp-operations.csv" };
        return response;
    }

}
