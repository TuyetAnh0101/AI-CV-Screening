using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Export;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Export;

namespace SmartCv.Api.Reports;

/// <summary>
/// Real PDF equivalent of the A31 ranking Excel export. The caller has already checked
/// workspace ownership, Run/Job, export limit, filters and run completion.
/// PDF never contains CV body, contact details, reasoning, API keys or Prompt text.
/// </summary>
public static class RankingPdfWriter
{
    public const string ContentType = "application/pdf";
    private const string Teal = "#174B53";
    private const string Slate = "#5D6E75";
    private const string Coral = "#B95D4A";

    public static byte[] Write(RankingResponse ranking, IReadOnlyList<RankingItemDto> rows,
        IReadOnlyDictionary<int, string> missingSkills, RankingExportQuery query, string actor, DateTime nowUtc)
    {
        if (ranking.RunId <= 0 || ranking.JobId <= 0 || rows.Count is < 1 or > 5000)
            throw new ArgumentException("Invalid ranking PDF scope.", nameof(rows));
        if (query.Format != "pdf") throw new ArgumentException("PDF format required.", nameof(query));
        if (string.IsNullOrWhiteSpace(actor)) actor = "Người dùng đã xác thực";

        // Keep identical license selection to HrRecruitmentReportPdfWriter (not a license bypass).
        // Deploying organization must confirm eligibility before production use.
        QuestPDF.Settings.License = LicenseType.Community;
        var issued = nowUtc.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var title = Safe(ranking.JobTitle, 100);
        var scope = Safe(query.ScopeText(), 200);
        var who = Safe(actor, 100);
        var document = Document.Create(root =>
        {
            root.Page(page =>
            {
                page.Size(PageSizes.A3.Landscape());
                page.Margin(30);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontFamily("Lato").FontSize(9).FontColor(Teal));

                page.Header().Column(header =>
                {
                    header.Item().Row(row =>
                    {
                        row.RelativeItem().Text("SCAPP  /  KẾT QUẢ & XẾP HẠNG").FontSize(11).Bold().FontColor("#6D59B6");
                        row.ConstantItem(220).AlignRight().Text($"Xuất lúc {issued} (UTC+7)").FontSize(8).FontColor(Slate);
                    });
                    header.Item().PaddingTop(7).LineHorizontal(1).LineColor("#DCE9E6");
                });
                page.Content().PaddingTop(14).Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Text($"BẢNG XẾP HẠNG — {title}").Bold().FontSize(17);
                    col.Item().Text($"Vị trí #{ranking.JobId} · Lượt phân tích #{ranking.RunId} · Prompt {Safe(ranking.PromptVersionName, 80)}")
                        .FontSize(9).FontColor(Slate);
                    col.Item().Background("#EAF5F3").Padding(12).Column(box =>
                    {
                        box.Spacing(4);
                        box.Item().Text($"Phạm vi: {scope}").SemiBold();
                        box.Item().Text($"Xuất {rows.Count:N0} trên {ranking.Items.Count:N0} hồ sơ · Ẩn danh: {(query.Anonymous ? "Có" : "Không")} · Người xuất: {who}").FontSize(8.5f);
                        box.Item().Text($"Ngưỡng đạt AI: {ranking.PassThreshold.ToString("0.##", CultureInfo.InvariantCulture)} điểm · Thứ tự: điểm cuối giảm dần, giữ hạng gốc").FontSize(8.5f);
                    });
                    col.Item().Text(ExcelRankingWriter.Disclaimer).FontSize(8.5f).Bold().FontColor(Coral);
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(43); // Original rank
                            columns.RelativeColumn(2.1f); // Applicant
                            columns.ConstantColumn(58); // Keyword
                            columns.ConstantColumn(51); // AI
                            columns.ConstantColumn(57); // Final
                            columns.RelativeColumn(1.15f); // Fit
                            columns.RelativeColumn(1.35f); // Source
                            columns.RelativeColumn(1.25f); // Stage
                            columns.RelativeColumn(2.45f); // Missing skills / reject reason
                        });
                        table.Header(header =>
                        {
                            foreach (var title in new[] { "Hạng", "Ứng viên", "Keyword", "AI", "Điểm cuối", "Mức", "Nguồn điểm", "Giai đoạn", "Thiếu kỹ năng / Lý do loại" })
                                header.Cell().Element(TableHeader).Text(title);
                        });
                        for (var i = 0; i < rows.Count; i++)
                        {
                            var item = rows[i];
                            var scored = item.Status == EvaluationStatuses.Completed && item.FinalScore is not null;
                            var bg = i % 2 == 0 ? "#FFFFFF" : "#F4F8F7";
                            void Cell(string? text) => table.Cell().Element(c => TableBody(c, bg)).Text(Safe(text, 220));
                            Cell(item.Rank?.ToString(CultureInfo.InvariantCulture) ?? "—");
                            Cell(query.Anonymous ? RankingExportQuery.Alias(item.Rank, item.ApplicationId)
                                : item.CandidateName ?? $"(chưa có tên) {item.FileName}");
                            Cell(Score(scored ? item.KeywordScore : null));
                            Cell(Score(scored ? item.AiScore : null));
                            Cell(Score(scored ? item.FinalScore : null));
                            Cell(!scored ? "Chưa chấm / lỗi" : item.FitLabel is FitLevels.Excellent or FitLevels.Good or FitLevels.Potential or FitLevels.Poor
                                ? item.FitLabel : "Mức không xác định");
                            Cell(!scored ? "—" : item.ScoreSource == ScoreSources.KeywordOnly ? "Chỉ từ khóa (AI lỗi)" : "Keyword + AI");
                            Cell(ApplicationStages.IsValid(item.Stage) ? ApplicationStages.DisplayName(item.Stage) : "Không xác định");
                            var missing = scored ? missingSkills.GetValueOrDefault(item.EvaluationId, "—") : "Chưa có kết quả kỹ năng";
                            var rejection = RejectReasons.IsValid(item.RejectReason) ? RejectReasons.DisplayName(item.RejectReason) : null;
                            Cell(rejection is null ? missing : $"{missing} · Loại: {rejection}");
                        }
                    });
                });
                page.Footer().PaddingTop(6).BorderTop(1).BorderColor("#DCE9E6").Row(row =>
                {
                    row.RelativeItem().Text("SCApp · Tài liệu nội bộ · Chỉ sử dụng trong workspace được cấp quyền").FontSize(8).FontColor(Slate);
                    row.ConstantItem(110).AlignRight().Text(t =>
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

    private static string Score(decimal? n) => n?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—";
    private static string Safe(string? input, int max)
    {
        var chars = (input ?? "—").Where(c => !char.IsControl(c)).Take(max).ToArray();
        return chars.Length == 0 ? "—" : new string(chars);
    }
    private static IContainer TableHeader(IContainer box) =>
        box.Background(Teal).Padding(7).DefaultTextStyle(t => t.FontColor(Colors.White).FontSize(8).SemiBold());
    private static IContainer TableBody(IContainer box, string color) =>
        box.Background(color).PaddingVertical(6).PaddingHorizontal(7)
            .BorderBottom(0.3f).BorderColor("#E0EAE7").DefaultTextStyle(t => t.FontSize(8));

    public static string FileName(int runId, int jobId, DateTime nowUtc) =>
        $"xep-hang-luot-{runId}-vi-tri-{jobId}-{nowUtc.AddHours(7):yyyyMMdd-HHmmss}.pdf";
}
