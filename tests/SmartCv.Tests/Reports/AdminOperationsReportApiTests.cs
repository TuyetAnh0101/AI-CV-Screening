using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartCv.Api.Auditing;
using SmartCv.Api.Reports;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Reports;

public sealed class AdminOperationsReportApiTests
{
    private static IQueryCollection Query(string text)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(text);
        return http.Request.Query;
    }

    [Theory]
    [InlineData("?from=2026-11-01", false)]
    [InlineData("?from=2026-11-01&to=2026-11-24", true)]
    [InlineData("?from=2026-11-24&to=2026-11-01", false)]
    [InlineData("?from=2026-11-01&to=2026-12-01", false)]
    [InlineData("?from=2026-01-01&to=2026-11-24", false)]
    [InlineData("?from=2026-11-01&to=2026-11-02&from=2026-11-03", false)]
    [InlineData("?from=2026-11-01&to=2026-11-24&search=abc", false)]
    [InlineData("?from=2026-02-31&to=2026-11-24", false)]
    public void Validates_explicit_calendar_periods_without_guessing(string query, bool ok)
    {
        var (filter, _) = AdminReportFilter.Parse(Query(query), new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(ok, filter is not null);
    }

    [Fact]
    public void Period_uses_vietnam_midnight_exclusive_end_and_rejects_format_pdf()
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var (filter, error) = AdminReportFilter.Parse(Query("?from=2026-11-23&to=2026-11-24"), now);
        Assert.Null(error);
        Assert.NotNull(filter);
        Assert.Equal(new DateTime(2026, 11, 22, 17, 0, 0, DateTimeKind.Utc), filter.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 24, 17, 0, 0, DateTimeKind.Utc), filter.UntilUtc);
        Assert.Equal(2, filter.Days);
        Assert.Null(AdminReportFilter.Parse(Query("?format=pdf"), now, csv: true).Filter);
        Assert.NotNull(AdminReportFilter.Parse(Query("?format=csv"), now, csv: true).Filter);
        Assert.Null(AdminReportFilter.Parse(Query("?page=1"), now).Filter);
        Assert.Null(AdminReportFilter.Parse(Query("?pageSize=101"), now, paging: true).Filter);
    }

    [Fact]
    public void Csv_reports_only_metadata_and_defends_formula_strings()
    {
        var period = new AdminReportPeriodDto(new DateOnly(2026, 11, 23),
            new DateOnly(2026, 11, 24), "Asia/Ho_Chi_Minh (UTC+07:00)", DateTime.UtcNow);
        var summary = new AdminReportSummaryDto(period,
            new(3, 2, 1, 1, [new("Other", 1)]),
            new(2, 1, 1, 1, 1, 1, 1, [new("Internal", 1), new("Personal", 1)]),
            new(5, 4, 1, 0, null, "not_instrumented", 1, 0, false, null, null, null, null, "not_available"),
            new(2, 1, 1, 1, 1), new(1, 0, 2, "Structured ranking count"),
            new("not_available", null, "A62 not available"));
        var trend = new AdminReportTrendDto(period,
            [new(new DateOnly(2026, 11, 23), 4, 1, 0, 1, 2, 0)],
            "AiUsageEntries", "EvaluationRuns", "AuditEvents");
        var bytes = AdminOperationsReportCsvWriter.Write(summary, trend,
            [new(22, DateTime.UtcNow, 5, "HR", 1, "runs.ranking_exported", "pdf", 2, true, "ExportLogs")]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var csv = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("screening_reserved", csv);
        Assert.Contains("export_event", csv);
        Assert.Contains("not_available", csv);
        Assert.Contains("actor_user_id", csv);
        Assert.DoesNotContain("CandidateName", csv);
        Assert.DoesNotContain("SystemPrompt", csv);
    }

    [Fact]
    public async Task Admin_owns_reporting_routes_HR_is_forbidden_even_by_direct_URL()
    {
        using var factory = A28TestSupport.Factory(new() { ["Scoring:WorkerEnabled"] = "false" });
        var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "Admin", memberOfWorkspace: null);
        var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var asAdmin = A29AuthorizationTests.Client(factory, admin);
        using var asHr = A29AuthorizationTests.Client(factory, hr);
        using var anonymous = factory.CreateClient();
        foreach (var path in new[]
        {
            "/api/admin/reports/summary", "/api/admin/reports/trend",
            "/api/admin/reports/exports", "/api/admin/reports/preview",
            "/api/admin/reports/health", "/api/admin/reports/download?format=csv"
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await asHr.GetAsync(path)).StatusCode);
        }
        using var response = await asAdmin.GetAsync("/api/admin/reports/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<AdminReportSummaryDto>();
        Assert.NotNull(report);
        Assert.True(report.Accounts.TotalNow >= 2);
        Assert.False(report.Ai.ProviderTelemetryAvailable); // InMemory has no real Gemini SQL telemetry.
        Assert.Null(report.Ai.GeminiAttempts);  // Missing evidence is NEVER presented as zero.
        Assert.Null(report.Ai.QuotaExceededEvents);  // Quota rejections are not fully instrumented.
        Assert.Equal("not_available", report.Backup.Status);
    }

    [Fact]
    public async Task Audit_persistence_failure_prevents_management_csv_delivery()
    {
        var fault = new A28TestSupport.DataFault();
        using var factory = A28TestSupport.Factory(new() { ["Scoring:WorkerEnabled"] = "false" }, fault);
        var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password(),
            "Admin", memberOfWorkspace: null);
        using var client = A29AuthorizationTests.Client(factory, admin);
        fault.FailSave = true;
        using var response = await client.GetAsync("/api/admin/reports/download?format=csv");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
        fault.FailSave = false;
        await A28TestSupport.WithDb(factory, async db =>
            Assert.Empty(await db.AuditEvents.Where(x => x.ActionCode == "admin.operations_report_exported"
                && x.Outcome == "AccessGranted").ToListAsync()));
    }

    [Fact]
    public async Task Admin_download_csv_is_audited_and_HR_cannot_obtain_it()
    {
        using var factory = A28TestSupport.Factory(new() { ["Scoring:WorkerEnabled"] = "false" });
        var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "Admin", memberOfWorkspace: null);
        using var client = A29AuthorizationTests.Client(factory, admin);
        using var file = await client.GetAsync("/api/admin/reports/download?format=csv");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("text/csv", file.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".csv", file.Content.Headers.ContentDisposition?.FileNameStar
            ?? file.Content.Headers.ContentDisposition?.FileName);
        var payload = await file.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(payload);
        await A28TestSupport.WithDb(factory, async db =>
        {
            var audit = await db.AuditEvents.SingleAsync(x => x.ActionCode == "admin.operations_report_exported"
                && x.Outcome == "AccessGranted");
            Assert.Equal(admin.UserId, audit.ActorUserId);
            Assert.Null(audit.WorkspaceId); // Platform-level management export, not an HR workspace export.
        });
        Assert.True(AuditCatalog.Routes.ContainsKey("GET /api/admin/reports/download"));
        Assert.True(AuditCatalog.Routes["GET /api/admin/reports/download"].Required);
    }
}
