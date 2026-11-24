using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Reports;

/// <summary>
/// PDF for the same authorized HR cohort as Excel; no CV body, AI prompt, email or contact details.
/// Each row represents exactly one Application. Uses Lato bundled by QuestPDF, which supports Vietnamese.
/// NOTE: Community license is permitted only if the deploying organization qualifies for it.
/// </summary>
public static class HrRecruitmentReportPdfWriter
{
    public const string ContentType = "application/pdf";
    private const string Teal = "#174B53";
    private const string Muted = "#647980";
    private const string Violet = "#6D59B6";
    private const string Coral = "#C96954";

    public static byte[] Write(HrReportSummaryDto summary, IReadOnlyList<HrReportRowDto> rows,
        string scope, bool anonymous, string actor, DateTime utcNow)
    {
        if (rows.Count > 2_000) throw new ArgumentOutOfRangeException(nameof(rows));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Actor required", nameof(actor));
        // For a student / qualifying community project. Production users must verify QuestPDF eligibility.
        QuestPDF.Settings.License = LicenseType.Community;
        var date = utcNow.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var document = Document.Create(root =>
        {
            root.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(29);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontFamily("Lato").FontSize(9).FontColor(Teal));

                page.Header().Column(header =>
                {
                    header.Item().Row(row =>
                    {
                        row.RelativeItem().Text("SCAPP  /  RECRUITMENT INTELLIGENCE").Bold().FontSize(11).FontColor(Violet);
                        row.ConstantItem(230).AlignRight().Text($"Xuất: {date} (UTC+7)").FontSize(8).FontColor(Muted);
                    });
                    header.Item().PaddingTop(7).LineHorizontal(1).LineColor("#DCE9E6");
                });

                page.Content().PaddingTop(16).Column(col =>
                {
                    col.Spacing(12);
                    col.Item().Text("BÁO CÁO TUYỂN DỤNG TỔNG HỢP").FontSize(18).Bold().FontColor(Teal);
                    col.Item().Text($"Phạm vi: {Safe(scope, 650)}").FontSize(8.5f).FontColor(Muted);
                    col.Item().Text($"Số bản ghi: {rows.Count:N0} lượt ứng tuyển  •  Chế độ: {(anonymous ? "Ẩn danh" : "Có tên ứng viên")}  •  Người xuất: {Safe(actor, 100)}")
                        .FontSize(9).SemiBold();
                    col.Item().Row(row =>
                    {
                        Metric(row, "LƯỢT ỨNG TUYỂN", summary.Applications.ToString("N0"), "#EAF5F3", Teal);
                        Metric(row, "CV DUY NHẤT", summary.UniqueCvs.ToString("N0"), "#F0ECFA", Violet);
                        Metric(row, "ỨNG VIÊN ĐÃ XÁC ĐỊNH", summary.KnownUniqueCandidates.ToString("N0"), "#EFF6FB", "#28789D");
                        Metric(row, "QUA VÒNG CV (LỊCH SỬ)", summary.EverPassedCvRound.ToString("N0"), "#FFF3ED", Coral);
                        Metric(row, "CẦN XỬ LÝ", summary.NeedsAction.ToString("N0"), "#FFF7E6", "#9D6C16");
                    });
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Background("#F7F9F9").Padding(10).Column(stat =>
                        {
                            stat.Item().Text("PHÂN BỐ GIAI ĐOẠN HIỆN TẠI").Bold().FontSize(9);
                            foreach (var stage in summary.Stages)
                            {
                                stat.Item().PaddingTop(4).Text($"{Safe(stage.Label, 42)}: {stage.Count:N0}").FontSize(8);
                            }
                        });
                        row.ConstantItem(12);
                        row.RelativeItem().Background("#F7F9F9").Padding(10).Column(stat =>
                        {
                            stat.Item().Text("CHẤT LƯỢNG & QUYẾT ĐỊNH").Bold().FontSize(9);
                            stat.Item().PaddingTop(4).Text($"Điểm Screening trung bình: {(summary.AverageFinalScore?.ToString("0.##", CultureInfo.InvariantCulture) ?? "Chưa có")}");
                            stat.Item().Text($"Đạt ngưỡng AI: {summary.PassedScore} / Dưới ngưỡng: {summary.BelowThreshold}");
                            stat.Item().Text($"Tỷ lệ đã qua vòng CV: {summary.HrPassedPercent:0.##}%");
                            stat.Item().Text($"Tỷ lệ đang bị loại: {summary.CurrentRejectedPercent:0.##}%");
                            stat.Item().Text($"Giờ đến quyết định đầu: {(summary.AverageHoursToFirstDecision?.ToString("0.##", CultureInfo.InvariantCulture) ?? "Chưa có")}");
                        });
                    });
                    col.Item().PaddingTop(5).Text("CHI TIẾT HỒ SƠ  /  MỘT DÒNG = MỘT LƯỢT ỨNG TUYỂN")
                        .FontSize(10).Bold();
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(48);
                            columns.RelativeColumn(2.1f);
                            columns.RelativeColumn(2.2f);
                            columns.RelativeColumn(1.5f);
                            columns.ConstantColumn(55);
                            columns.RelativeColumn(1.4f);
                            columns.RelativeColumn(1.3f);
                        });
                        table.Header(header =>
                        {
                            header.Cell().Element(HeaderStyle).Text("ID");
                            header.Cell().Element(HeaderStyle).Text("Hồ sơ");
                            header.Cell().Element(HeaderStyle).Text("Vị trí");
                            header.Cell().Element(HeaderStyle).Text("Giai đoạn HR");
                            header.Cell().Element(HeaderStyle).Text("Điểm");
                            header.Cell().Element(HeaderStyle).Text("Mức phù hợp");
                            header.Cell().Element(HeaderStyle).Text("Đạt ngưỡng");
                        });
                        for (var index = 0; index < rows.Count; index++)
                        {
                            var r = rows[index];
                            var bg = index % 2 == 0 ? "#FFFFFF" : "#F4F8F7";
                            table.Cell().Element(c => BodyStyle(c, bg)).Text(r.ApplicationId.ToString(CultureInfo.InvariantCulture));
                            table.Cell().Element(c => BodyStyle(c, bg)).Text(Safe(r.Applicant, 80));
                            table.Cell().Element(c => BodyStyle(c, bg)).Text(Safe(r.JobTitle, 90));
                            table.Cell().Element(c => BodyStyle(c, bg)).Text(Safe(r.StageText, 44));
                            table.Cell().Element(c => BodyStyle(c, bg)).Text(r.FinalScore?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—");
                            table.Cell().Element(c => BodyStyle(c, bg)).Text(Safe(r.FitLabel, 45));
                            table.Cell().Element(c => BodyStyle(c, bg)).Text(r.PassedScore switch { true => "Đạt", false => "Dưới", _ => "Chưa chấm" });
                        }
                    });
                    col.Item().Text("Lưu ý: điểm AI là chỉ số hỗ trợ; kết quả tuyển dụng thuộc quyết định của HR. Không cộng lẫn lượt ứng tuyển, CV và ứng viên; không bao gồm PromptTest.")
                        .FontSize(8).FontColor(Muted);
                });
                page.Footer().PaddingTop(6).BorderTop(1).BorderColor("#DCE9E6").Row(row =>
                {
                    row.RelativeItem().Text("SCApp • Báo cáo nội bộ • Chỉ sử dụng trong workspace được cấp quyền").FontSize(7.5f).FontColor(Muted);
                    row.ConstantItem(100).AlignRight().Text(t =>
                    {
                        t.Span("Trang ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });
        });
        return document.GeneratePdf();
    }

    private static string Safe(string? input, int max) =>
        new((input ?? "—").Where(c => !char.IsControl(c)).Take(max).ToArray());

    private static void Metric(RowDescriptor row, string title, string value, string bg, string fg)
    {
        row.RelativeItem().PaddingRight(5).Background(bg).Padding(10).Column(col =>
        {
            col.Item().Text(title).FontSize(7).SemiBold().FontColor(fg);
            col.Item().PaddingTop(4).Text(value).FontSize(17).Bold().FontColor(fg);
        });
    }

    private static IContainer HeaderStyle(IContainer box) =>
        box.Background(Teal).Padding(6).DefaultTextStyle(t => t.FontColor(Colors.White).Bold().FontSize(8));

    private static IContainer BodyStyle(IContainer box, string background) =>
        box.Background(background).PaddingVertical(5).PaddingHorizontal(6)
            .BorderBottom(0.25f).BorderColor("#E3EAE8")
            .DefaultTextStyle(t => t.FontSize(8));

    public static string Filename(DateTime nowUtc) =>
        $"scapp-bao-cao-tuyen-dung-{nowUtc.AddHours(7):yyyyMMdd-HHmmss}.pdf";
}
