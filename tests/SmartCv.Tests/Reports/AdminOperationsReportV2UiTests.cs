extern alias web;

using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using web::SmartCv.Web.Components.Pages;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests.Reports;

public sealed class AdminOperationsReportV2UiTests
{
    [Fact]
    public void Reporting_page_remains_admin_only_while_v2_is_an_embedded_component()
    {
        Assert.Contains(typeof(AdminOperationsReport).GetCustomAttributes<RouteAttribute>(),
            x => x.Template == "/admin/reports");
        Assert.Contains(typeof(AdminOperationsReport).GetCustomAttributes<AuthorizeAttribute>(),
            x => x.Policy == AccessRules.AdminPolicy);
        Assert.Empty(typeof(web::SmartCv.Web.Components.Reports.AdminReportV2Export)
            .GetCustomAttributes<RouteAttribute>());
    }

    [Theory]
    [InlineData("all", "xlsx")]
    [InlineData("ai", "csv")]
    [InlineData("security", "xlsx")]
    public void Query_is_strict_and_date_local(string report, string format)
    {
        var q = AdminOperationsReportV2ApiClient.Query(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 25), report, format);
        Assert.Equal($"?from=2026-11-01&to=2026-11-25&report={report}&format={format}", q);
        Assert.DoesNotContain("token", q, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate", q, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_unimplemented_formats_and_report_dimensions()
    {
        Assert.Throws<ArgumentException>(() => AdminOperationsReportV2ApiClient.Query(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 25), "all", "pdf"));
        Assert.Throws<ArgumentException>(() => AdminOperationsReportV2ApiClient.Query(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 25), "cv-details", "xlsx"));
    }

    [Fact]
    public async Task Client_cannot_send_a_report_without_a_verified_tab_session()
    {
        var handler = new B27CaptureHandler();
        await using var services = B27.WebServices(handler);
        using var scope = services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<AdminOperationsReportV2ApiClient>();
        var response = await api.PreviewAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 25), "all", "xlsx");
        Assert.False(response.IsSuccess);
        Assert.Equal(ApiFailureKind.SignedOut, response.FailureKind);
        Assert.Empty(handler.Calls);
    }
}
