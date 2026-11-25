using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartCv.Api.Reports;
using SmartCv.Core.Contracts;
using SmartCv.Infrastructure.Export;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Reports;

public sealed class AdminOperationsReportV2Tests
{
    private static IQueryCollection Query(string text)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(text);
        return http.Request.Query;
    }

    [Theory]
    [InlineData("?report=all&format=xlsx", true)]
    [InlineData("?report=accounts&format=csv", true)]
    [InlineData("?report=security&format=xlsx", true)]
    [InlineData("?report=all&format=pdf", false)]
    [InlineData("?report=all&format=xlsx&format=csv", false)]
    [InlineData("?report=users&format=csv", false)]
    [InlineData("?report=all&format=xlsx&workspaceId=1", false)]
    [InlineData("?from=2026-11-23", false)]
    [InlineData("?from=2026-08-01&to=2026-11-24", false)]
    public void V2_request_is_strict_and_preserves_v1_period_rules(string input, bool accepted)
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var parsed = AdminReportV2Filter.Parse(Query(input), now).Filter;
        Assert.Equal(accepted, parsed is not null);
    }

    private static AdminReportSummaryDto Summary(long newAccounts = 2)
    {
        var period = new AdminReportPeriodDto(new DateOnly(2026, 11, 20),
            new DateOnly(2026, 11, 24), "Asia/Ho_Chi_Minh (UTC+07:00)", DateTime.UtcNow);
        return new(period,
            new(10, 8, 2, newAccounts, [new("Local", 7), new("google", 3)]),
            new(5, 4, 1, 3, 2, 2, 1, [new("Internal", 2), new("Personal", 3)]),
            new(9, 7, 1, 1, null, "not_instrumented", 2, 3, false,
                null, null, null, null, "telemetry_unavailable"),
            new(3, 0, 4, 1, 2),
            new(1, 0, 12, "only ranking structured rows"),
            new("not_available", null, "A62 pending"));
    }

    [Fact]
    public void Xlsx_workbook_is_openable_and_keeps_unavailable_as_unavailable()
    {
        var summary = Summary();
        var prev = Summary(0);
        var trend = new AdminReportTrendDto(summary.Period,
            [new(new DateOnly(2026, 11, 23), 4, 1, 0, 1, 2, null)],
            "AiUsageEntries", "EvaluationRuns", "AuditEvents");
        var export = new AdminReportExportEventDto(88, DateTime.UtcNow, 7,
            "HR", 2, "runs.ranking_exported", "xlsx", 2, true, "ExportLogs");
        var bytes = AdminOperationsReportExcelWriter.Write(summary, prev, trend,
            [export], null, "all", DateTime.UtcNow);
        Assert.True(bytes.Length > 1200);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var names = workbook.Worksheets.Select(x => x.Name).ToArray();
        Assert.Contains("01 Tong quan", names);
        Assert.Contains("02 Tai khoan", names);
        Assert.Contains("03 Workspace", names);
        Assert.Contains("04 AI & Quota", names);
        Assert.Contains("05 Van hanh", names);
        Assert.Contains("06 Bao mat", names);
        Assert.Contains("07 Nhat ky xuat", names);
        Assert.Contains("08 Nguon du lieu", names);
        Assert.NotEmpty(workbook.Worksheet("07 Nhat ky xuat").CellsUsed());
        var allText = string.Join("|", workbook.Worksheets
            .SelectMany(x => x.CellsUsed().Select(c => c.GetFormattedString())));
        Assert.DoesNotContain("CandidateName", allText);
        Assert.DoesNotContain("SystemPrompt", allText);
        Assert.Contains("Chưa có dữ liệu", allText);
        Assert.Null(AdminOperationsReportExcelWriter.Compare(summary, prev)
            .Single(x => x.Metric == "Tài khoản mới").ChangePercent);
    }

    [Fact]
    public void Selected_csv_has_only_requested_category_and_defends_formula_tokens()
    {
        var s = Summary();
        var trend = new AdminReportTrendDto(s.Period, [], "AiUsageEntries", "EvaluationRuns", "AuditEvents");
        var bytes = AdminOperationsReportV2CsvWriter.Write(s, Summary(0), trend, [], null, "ai");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("net_reserved_units", text);
        Assert.Contains("quota_rejection_events", text);
        Assert.DoesNotContain("failed_password_logins", text);
        Assert.DoesNotContain("CandidateName", text);
    }

    private static HttpClient Admin(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        SmartCv.Core.Entities.User user)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", A28TestSupport.Token(user, workspaceId: null));
        return http;
    }

    [Fact]
    public async Task V2_requires_real_admin_and_preview_limits_apply_only_to_exports()
    {
        using var factory = A28TestSupport.Factory(new() { ["Scoring:WorkerEnabled"] = "false" });
        var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "Admin", memberOfWorkspace: null);
        var hr = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR");
        using var anonymous = factory.CreateClient();
        using var asAdmin = Admin(factory, admin);
        using var asHr = A29AuthorizationTests.Client(factory, hr);
        const string route = "/api/admin/reports/v2/preview?report=accounts&format=xlsx";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asHr.GetAsync(route)).StatusCode);
        using var ok = await asAdmin.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var preview = await ok.Content.ReadFromJsonAsync<AdminReportV2PreviewDto>();
        Assert.NotNull(preview);
        Assert.True(preview.CanDownload);
        Assert.Null(preview.ExportEventCount); // Not '0 exports'. Journal was not selected.
        Assert.Equal("accounts", preview.Report);
    }

    [Fact]
    public async Task Xlsx_export_is_audited_and_a_write_failure_prevents_delivery()
    {
        var fault = new A28TestSupport.DataFault();
        using var factory = A28TestSupport.Factory(new() { ["Scoring:WorkerEnabled"] = "false" }, fault);
        var admin = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "Admin", memberOfWorkspace: null);
        using var client = Admin(factory, admin);
        const string path = "/api/admin/reports/v2/download?report=accounts&format=xlsx";
        fault.FailSave = true;
        using (var blocked = await client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            Assert.Null(blocked.Content.Headers.ContentDisposition);
        }
        fault.FailSave = false;
        using (var ok = await client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(AdminOperationsReportExcelWriter.ContentType,
                ok.Content.Headers.ContentType?.MediaType);
            Assert.EndsWith(".xlsx", ok.Content.Headers.ContentDisposition?.FileNameStar
                ?? ok.Content.Headers.ContentDisposition?.FileName);
            using var workbook = new XLWorkbook(new MemoryStream(await ok.Content.ReadAsByteArrayAsync()));
            Assert.Contains(workbook.Worksheets, s => s.Name == "02 Tai khoan");
            Assert.DoesNotContain(workbook.Worksheets, s => s.Name == "07 Nhat ky xuat");
        }
        await A28TestSupport.WithDb(factory, async db =>
        {
            var record = await db.AuditEvents.SingleAsync(x =>
                x.ActionCode == "admin.operations_report_exported" && x.Outcome == "AccessGranted");
            Assert.Equal(admin.UserId, record.ActorUserId);
            Assert.Null(record.WorkspaceId);
            Assert.Contains("accounts/xlsx", record.SafeSummary);
        });
    }
}
