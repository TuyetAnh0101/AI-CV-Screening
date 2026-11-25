using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using SmartCv.Core.Contracts;

namespace SmartCv.Infrastructure.Export;

/// <summary>
/// Global operational METADATA only. Never accepts candidate, CV, PromptVersion, email or score entities.
/// The workbook is generated server-side; JS gets file bytes, never an access token.
/// </summary>
public static class AdminOperationsReportExcelWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly XLColor Teal = XLColor.FromHtml("#123E47");
    private static readonly XLColor Violet = XLColor.FromHtml("#6949A5");
    private static readonly XLColor Light = XLColor.FromHtml("#EDF6F5");
    private static readonly XLColor Amber = XLColor.FromHtml("#A15B16");

    public static string FileName(DateTime capturedAtUtc, string kind) =>
        $"scapp-admin-{kind}-{capturedAtUtc.AddHours(7):yyyyMMdd-HHmmss}.xlsx";

    public static byte[] Write(AdminReportSummaryDto current, AdminReportSummaryDto previous,
        AdminReportTrendDto trend, IReadOnlyList<AdminReportExportEventDto> exports,
        AdminReportHealthDto? health, string kind, DateTime capturedAtUtc)
    {
        if (!AdminReportV2Kinds.Allowed.Contains(kind, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported report selection.", nameof(kind));
        using var book = new XLWorkbook();
        book.Properties.Author = "SCApp";
        book.Properties.Title = "SCApp - Admin Operations (metadata only)";
        Overview(book, current, previous, kind, capturedAtUtc);
        if (Include(kind, AdminReportV2Kinds.Accounts)) Accounts(book, current);
        if (Include(kind, AdminReportV2Kinds.Workspaces)) Workspaces(book, current);
        if (Include(kind, AdminReportV2Kinds.Ai)) Ai(book, current, trend);
        if (Include(kind, AdminReportV2Kinds.Operations)) Operations(book, health);
        if (Include(kind, AdminReportV2Kinds.Security)) Security(book, current, trend);
        if (Include(kind, AdminReportV2Kinds.Exports)) Exports(book, exports);
        Sources(book, current);
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static bool Include(string selected, string section) =>
        selected == AdminReportV2Kinds.All || selected == section;

    private static IXLWorksheet New(XLWorkbook book, string name, string title, string subtitle)
    {
        var sheet = book.Worksheets.Add(name);
        sheet.Cell(1, 1).Value = title;
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 17;
        sheet.Cell(1, 1).Style.Font.FontColor = Teal;
        sheet.Cell(2, 1).Value = Clean(subtitle);
        sheet.Cell(2, 1).Style.Font.FontColor = Violet;
        sheet.Row(1).Height = 27;
        sheet.Row(2).Height = 23;
        sheet.Column(1).Width = 42;
        sheet.Column(2).Width = 27;
        sheet.Column(3).Width = 28;
        sheet.Column(4).Width = 33;
        sheet.Column(5).Width = 36;
        sheet.Column(6).Width = 37;
        return sheet;
    }

    private static void Table(IXLWorksheet sheet, string[] headings, IEnumerable<object?[]> rows,
        int startRow = 4)
    {
        for (var i = 0; i < headings.Length; i++)
        {
            var cell = sheet.Cell(startRow, i + 1);
            cell.Value = headings[i];
            cell.Style.Fill.BackgroundColor = Teal;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.WrapText = true;
        }
        sheet.Row(startRow).Height = 28;
        var row = startRow + 1;
        foreach (var values in rows)
        {
            for (var i = 0; i < Math.Min(values.Length, headings.Length); i++)
            {
                var cell = sheet.Cell(row, i + 1);
                switch (values[i])
                {
                    case long n: cell.Value = n; break;
                    case int n: cell.Value = n; break;
                    case decimal n: cell.Value = (double)n; break;
                    case DateTime date: cell.Value = date.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture); break;
                    case DateOnly date: cell.Value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                    case null: cell.Value = "Chưa có dữ liệu"; cell.Style.Font.FontColor = Amber; break;
                    default: cell.Value = Clean(Convert.ToString(values[i], CultureInfo.InvariantCulture)); break;
                }
                if (row % 2 == 1) cell.Style.Fill.BackgroundColor = Light;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }
            row++;
        }
        // Filters are on bounded, already-materialized metadata rows; no extra DB reads.
        sheet.Range(startRow, 1, Math.Max(startRow + 1, row - 1), headings.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(startRow);
    }

    private static string Clean(string? value)
    {
        var sb = new StringBuilder();
        foreach (var rune in (value ?? "").EnumerateRunes())
        {
            if (rune.Value < 32 || rune.Value == 127) continue;
            if (sb.Length + rune.Utf16SequenceLength > 200) break;
            sb.Append(rune.ToString());
        }
        var result = sb.ToString();
        return result.Length > 0 && "=+-@\t\r".Contains(result[0]) ? "''" + result : result;
    }

    private static void Overview(XLWorkbook book, AdminReportSummaryDto now,
        AdminReportSummaryDto previous, string kind, DateTime capturedAtUtc)
    {
        var sh = New(book, "01 Tong quan", "SCApp | Báo cáo quản trị vận hành",
            $"Loại: {kind} | {now.Period.From:dd/MM/yyyy} - {now.Period.To:dd/MM/yyyy} (UTC+7)");
        sh.Cell(3, 1).Value = "Tài khoản/workspace là trạng thái hiện tại; so sánh kỳ chỉ áp dụng cho SỰ KIỆN phát sinh.";
        sh.Cell(3, 1).Style.Font.FontColor = Amber;
        List<object?[]> rows =
        [
            ["Phạm vi báo cáo", kind, "Danh mục điều hành", "metadata only"],
            ["Thời điểm lấy mẫu UTC", capturedAtUtc, "TimeProvider", "Không phải snapshot transaction"],
            ["Tài khoản được phép hoạt động (hiện tại)", now.Accounts.EnabledNow, "Users.IsActive", "Không phải online"],
            ["Workspace hoạt động (hiện tại)", now.Workspaces.ActiveNow, "Workspaces.IsActive", "Không phải hoạt động gần đây"],
            ["Tài khoản tạo trong kỳ", now.Accounts.CreatedInPeriod, "Users.CreatedAt", "So sánh kỳ trước bên dưới"],
            ["Quota giữ ròng trong kỳ", now.Ai.NetReservedUnits, "AiUsageEntries", "Không phải token Gemini"],
            ["Run chấm bị lỗi trong kỳ", now.Ai.FailedRuns, "EvaluationRuns", "Không đồng nghĩa lỗi Gemini"],
            ["Audit bị từ chối trong kỳ", now.Security.DeniedAuditEvents, "AuditEvents", "Chỉ các sự kiện đã ghi"],
            ["Gemini telemetry", now.Ai.ProviderTelemetryAvailable ? "Có nguồn" : "Không khả dụng", "GeminiInvocationLogs", "Không tự điền số 0"],
            ["Lượt vượt quota", now.Ai.QuotaExceededEvents, now.Ai.QuotaExceededSourceStatus, "Chưa có bộ đếm tin cậy"],
            ["Backup A62", now.Backup.Status, now.Backup.Source, "Không suy diễn thành công"],
            ["--- SO SÁNH HAI KỲ CÙNG ĐỘ DÀI ---", "Kỳ hiện tại", "Kỳ liền trước", "Biến động (%)"]
        ];
        foreach (var metric in Compare(now, previous))
            rows.Add([metric.Metric, metric.Current, metric.Previous,
                metric.ChangePercent is { } p ? p.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + "%" : "N/A (kỳ trước = 0)"]);
        Table(sh, ["Chỉ số", "Giá trị / Kỳ này", "Nguồn / Kỳ trước", "Giải thích / Biến động"], rows);
    }

    public static IReadOnlyList<AdminReportV2ComparisonRowDto> Compare(AdminReportSummaryDto current, AdminReportSummaryDto previous)
    {
        static AdminReportV2ComparisonRowDto Row(string label, long a, long b, string source) =>
            new(label, a, b, b == 0 ? null : Math.Round(100m * (a - b) / b, 2), source);
        return
        [
            Row("Tài khoản mới", current.Accounts.CreatedInPeriod, previous.Accounts.CreatedInPeriod, "Users.CreatedAt"),
            Row("Đơn vị quota giữ ròng", current.Ai.NetReservedUnits, previous.Ai.NetReservedUnits, "AiUsageEntries"),
            Row("Run xử lý lỗi", current.Ai.FailedRuns, previous.Ai.FailedRuns, "EvaluationRuns.Status"),
            Row("Sự kiện Audit bị từ chối", current.Security.DeniedAuditEvents, previous.Security.DeniedAuditEvents, "AuditEvents")
        ];
    }

    private static void Accounts(XLWorkbook book, AdminReportSummaryDto s)
    {
        var sh = New(book, "02 Tai khoan", "Tài khoản toàn hệ thống",
            "Trạng thái hiện tại, không phải phiên online; đăng ký mới theo kỳ lọc");
        List<object?[]> rows =
        [
            ["Tổng tài khoản", s.Accounts.TotalNow, "Users", "Snapshot"],
            ["Được phép hoạt động", s.Accounts.EnabledNow, "Users.IsActive = true", "Snapshot"],
            ["Đã khóa", s.Accounts.LockedNow, "Users.IsActive = false", "Snapshot"],
            ["Tạo trong kỳ", s.Accounts.CreatedInPeriod, "Users.CreatedAt", "Theo thời gian UTC+7"]
        ];
        rows.AddRange(s.Accounts.BySignupSource.Select(x => new object?[] { "Nguồn: " + x.Code, x.Count, "Users.SignupSource", "Toàn bộ tài khoản hiện tại" }));
        Table(sh, ["Chỉ tiêu", "Số lượng", "Nguồn", "Phạm vi"], rows);
    }

    private static void Workspaces(XLWorkbook book, AdminReportSummaryDto s)
    {
        var sh = New(book, "03 Workspace", "Workspace & giới hạn",
            "Phân loại Personal/Internal; giới hạn được cấu hình, không phản ánh mức dùng quota từng workspace");
        List<object?[]> rows =
        [
            ["Tổng workspace", s.Workspaces.TotalNow, "Workspaces", "Snapshot"],
            ["Đang kích hoạt", s.Workspaces.ActiveNow, "Workspaces.IsActive", "Snapshot"],
            ["Ngừng kích hoạt", s.Workspaces.InactiveNow, "Workspaces.IsActive", "Snapshot"],
            ["Personal", s.Workspaces.PersonalNow, "Workspaces.Kind", "Snapshot"],
            ["Internal", s.Workspaces.InternalNow, "Workspaces.Kind", "Snapshot"],
            ["Personal có giới hạn AI/ngày", s.Workspaces.PersonalWithDailyAiLimitNow, "DailyAiEvaluationLimit", "Giới hạn được thiết lập"],
            ["Personal có giới hạn CV", s.Workspaces.PersonalWithCvLimitNow, "MaxCvCount", "Giới hạn được thiết lập"]
        ];
        rows.AddRange(s.Workspaces.ByKind.Select(x => new object?[] { "Loại: " + x.Code, x.Count, "Workspaces.Kind", "Snapshot" }));
        Table(sh, ["Chỉ tiêu", "Số lượng", "Nguồn", "Ý nghĩa"], rows);
    }

    private static void Ai(XLWorkbook book, AdminReportSummaryDto s, AdminReportTrendDto trend)
    {
        var sh = New(book, "04 AI & Quota", "AI & Quota - nền tảng",
            "Ledger giữ ròng khác với Gemini HTTP attempts; retry có thể làm tăng số attempts");
        List<object?[]> rows =
        [
            ["Quota ròng", s.Ai.NetReservedUnits, "AiUsageEntries", "Units - UnitsReleased"],
            ["Screening", s.Ai.ScreeningUnits, "AiUsageEntries.Source", "Quota units"],
            ["PromptTest", s.Ai.PromptTestUnits, "AiUsageEntries.Source", "Quota units"],
            ["Preview", s.Ai.PreviewUnits, "AiUsageEntries.Source", "Quota units"],
            ["Run xử lý lỗi", s.Ai.FailedRuns, "EvaluationRuns", "Không suy ra provider lỗi"],
            ["Evaluation lỗi", s.Ai.FailedEvaluations, "Evaluations", "Số bản ghi lỗi"],
            ["Gemini attempts", s.Ai.GeminiAttempts, s.Ai.ProviderSourceStatus, "Retries được tính riêng"],
            ["Gemini HTTP OK", s.Ai.GeminiHttpOk, "GeminiInvocationLogs", "Chỉ khi có telemetry"],
            ["Gemini error/timeout", s.Ai.GeminiErrorsOrTimeouts, "GeminiInvocationLogs", "Chỉ khi có telemetry"],
            ["Gemini cancelled", s.Ai.GeminiCancelled, "GeminiInvocationLogs", "Chỉ khi có telemetry"],
            ["Quota bị từ chối", s.Ai.QuotaExceededEvents, s.Ai.QuotaExceededSourceStatus, "Chưa có counter bền vững"]
        ];
        Table(sh, ["Chỉ tiêu", "Giá trị", "Nguồn", "Giải thích"], rows);
        var days = New(book, "04B AI theo ngay", "Xu hướng AI theo ngày (UTC+7)",
            "Dữ liệu đã lưu; số 0 là không có sự kiện từ nguồn đọc được, không có telemetry quota từ chối");
        Table(days, ["Ngày", "Screening", "PromptTest", "Preview", "Run lỗi", "Quota bị từ chối"],
            trend.Points.Select(p => new object?[]
            { p.Date, p.ScreeningUnits, p.PromptTestUnits, p.PreviewUnits, p.FailedRuns, p.QuotaExceededEvents }));
    }

    private static void Operations(XLWorkbook book, AdminReportHealthDto? health)
    {
        var sh = New(book, "05 Van hanh", "Sức khỏe tại thời điểm kiểm tra",
            "Không tính uptime/SLA; email chỉ kiểm cấu hình; worker không có heartbeat");
        var rows = new List<object?[]>();
        if (health is null) rows.Add(["Health", "Chưa có phép kiểm tra", "Not available", "Không suy diễn"]);
        else
        {
            rows.Add(["Thời điểm (UTC)", health.CheckedAtUtc, "ApplicationReadiness", "Snapshot"]);
            rows.Add(["Readiness", health.ReadinessStatus, "ApplicationReadiness", "Không phải uptime"]);
            rows.AddRange(health.Checks.Select(c => new object?[]
                { "Dependency: " + c.Name, c.Ok ? "Sẵn sàng" : "Cần kiểm tra", "ApplicationReadiness", c.Message }));
            rows.AddRange(health.WorkerConfiguration.Select(w => new object?[]
                { "Cấu hình: " + w.Name, w.Enabled ? "Enabled" : "Disabled", "AppSettings", w.Meaning }));
            rows.Add(["API uptime", health.ApiUptimeStatus, "not_monitored", "Không có lịch sử health"]);
            rows.Add(["Email", health.EmailCheckScope, "EmailOptions", "Không test SMTP"]);
            rows.Add(["Backup", health.BackupStatus, "A62", "Chưa có nguồn backup"]);
        }
        Table(sh, ["Kiểm tra", "Kết quả", "Nguồn", "Giải thích"], rows);
    }

    private static void Security(XLWorkbook book, AdminReportSummaryDto s, AdminReportTrendDto trend)
    {
        var sh = New(book, "06 Bao mat", "Bảo mật & nhật ký kiểm soát",
            "Chỉ các sự kiện Audit đã lưu; không phải tổng số request HTTP bị chặn");
        Table(sh, ["Chỉ tiêu", "Số sự kiện", "Nguồn", "Phạm vi"],
        [
            ["Đăng nhập mật khẩu thất bại", s.Security.FailedPasswordLogins, "AuditEvents.auth.login", "Trong kỳ"],
            ["External callbacks bị từ chối", s.Security.RejectedExternalCallbacks, "AuditEvents", "Trong kỳ"],
            ["Audit từ chối", s.Security.DeniedAuditEvents, "AuditEvents.Outcome", "Trong kỳ"],
            ["Truy cập bị từ chối (đã lưu)", s.Security.AccessDeniedAuditEvents, "AuditEvents.FailureCode", "Trong kỳ"],
            ["Thao tác do Admin", s.Security.AdministrativeAuditEvents, "AuditEvents.ActorRoleSnapshot", "Trong kỳ"]
        ]);
        var days = New(book, "06B Bao mat ngay", "Audit bị từ chối theo ngày", "Sự kiện đã ghi nhận theo ngày UTC+7");
        Table(days, ["Ngày", "Audit bị từ chối"],
            trend.Points.Select(p => new object?[] { p.Date, p.DeniedAuditEvents }));
    }

    private static void Exports(XLWorkbook book, IReadOnlyList<AdminReportExportEventDto> exports)
    {
        var sh = New(book, "07 Nhat ky xuat", "Nhật ký xuất dữ liệu",
            "Giới hạn 1.000 sự kiện; RowCount trống khi hệ thống không ghi nhận cấu trúc");
        Table(sh, ["AuditEventId", "Thời điểm UTC", "ActorUserId", "Vai trò",
                   "WorkspaceId", "ActionCode", "Format", "RowCount", "Ẩn danh", "Nguồn row-count"],
            exports.Select(x => new object?[]
            { x.AuditEventId, x.OccurredAtUtc, x.ActorUserId, x.ActorRole,
              x.WorkspaceId, x.Category, x.Format, x.RowCount,
              x.Anonymous is { } a ? (a ? "Có" : "Không") : "Không ghi", x.RowCountSource }));
        sh.Column(6).Width = 43; sh.Column(10).Width = 37;
    }

    private static void Sources(XLWorkbook book, AdminReportSummaryDto s)
    {
        var sh = New(book, "08 Nguon du lieu", "Nguồn số liệu & giới hạn đối chiếu",
            "Nội dung báo cáo chỉ là metadata. Các snapshot của nhiều truy vấn không đồng bộ giao dịch.");
        var rows = s.Sources.Select(x => new object?[] { x.Metric, x.Source, x.Basis }).ToList();
        rows.Add(["time_range", "Period.From/To", "Ngày địa phương UTC+7, bao gồm cả hai ngày"]);
        rows.Add(["current_snapshot", "Users/Workspaces", "Không tái tạo trạng thái lịch sử trong kỳ"]);
        rows.Add(["consistency", "Multiple DB queries", "Thời gian chốt nhất quán tương đối; không bảo đảm atomic DB snapshot"]);
        rows.Add(["quota_refunds", "AiUsageEntries", "Hoàn trả sau đó có thể làm thay đổi tổng quota ròng ngày cũ"]);
        rows.Add(["uptime", "not_monitored", "Readiness là trạng thái tức thời, KHÔNG phải tỷ lệ uptime"]);
        rows.Add(["backup", "A62", "Chưa có dữ liệu chứng minh sao lưu thành công"]);
        Table(sh, ["Chỉ số", "Nguồn", "Giới hạn/định nghĩa"], rows);
        sh.Column(3).Width = 85;
    }
}
