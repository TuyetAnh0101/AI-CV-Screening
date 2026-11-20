using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Export;
using SmartCv.Core.Scoring;

namespace SmartCv.Infrastructure.Export;

public static class ExcelRankingWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Disclaimer = "AI chỉ hỗ trợ, HR quyết định. Điểm và mức đánh giá là gợi ý, không phải kết luận tuyển dụng.";
    public static readonly string[] Headers = ["Hạng", "Ứng viên", "Điểm keyword", "Điểm AI", "Điểm cuối", "Mức", "Nguồn điểm", "Kỹ năng bắt buộc còn thiếu", "Giai đoạn", "Lý do loại"];

    public static byte[] Write(RankingResponse ranking, IReadOnlyList<RankingItemDto> rows,
        IReadOnlyDictionary<int, string> missingSkills, RankingExportQuery query, string actor, DateTime nowUtc)
    {
        using var book = new XLWorkbook();
        book.Properties.Author = "SCApp";
        var sheet = book.Worksheets.Add("Xep hang");
        sheet.Style.Font.FontName = "Arial";
        sheet.Style.Font.FontSize = 11;
        void Info(int row, string text)
        {
            sheet.Range(row, 1, row, Headers.Length).Merge();
            sheet.Cell(row, 1).Value = SafeText(text);
        }
        Info(1, $"Bảng xếp hạng – {ranking.JobTitle} – lượt chấm #{ranking.RunId}");
        sheet.Cell(1, 1).Style.Font.SetBold();
        sheet.Cell(1, 1).Style.Font.FontSize = 16;
        Info(2, $"Phạm vi: {query.ScopeText()} · {rows.Count}/{ranking.Items.Count} hồ sơ · Điểm cuối giảm dần");
        Info(3, $"Prompt: {ranking.PromptVersionName} · Ngưỡng đạt: {ranking.PassThreshold.ToString("0.#", CultureInfo.InvariantCulture)} điểm");
        Info(4, $"Xuất lúc {nowUtc.AddHours(7):dd/MM/yyyy HH:mm} (giờ Việt Nam) bởi {actor}");
        Info(5, Disclaimer);
        sheet.Cell(5, 1).Style.Font.FontColor = XLColor.DarkRed;
        const int headerRow = 7;
        for (var col = 0; col < Headers.Length; col++) sheet.Cell(headerRow, col + 1).Value = Headers[col];
        for (var index = 0; index < rows.Count; index++)
        {
            var r = rows[index]; var line = headerRow + 1 + index;
            var scored = r.Status == EvaluationStatuses.Completed && r.FinalScore is not null;
            void Text(int col, string? text) => sheet.Cell(line, col).Value = SafeText(text);
            void Number(int col, decimal? number) { if (number is { } n) sheet.Cell(line, col).Value = (double)n; }
            Number(1, r.Rank);
            Text(2, query.Anonymous ? RankingExportQuery.Alias(r.Rank, r.ApplicationId) : r.CandidateName ?? $"(chưa có tên) {r.FileName}");
            Number(3, scored ? r.KeywordScore : null); Number(4, scored ? r.AiScore : null); Number(5, scored ? r.FinalScore : null);
            Text(6, !scored ? "Chưa chấm xong / lỗi" : r.FitLabel is FitLevels.Excellent or FitLevels.Good or FitLevels.Potential or FitLevels.Poor ? r.FitLabel : "Mức không xác định");
            Text(7, !scored ? "—" : r.ScoreSource == ScoreSources.KeywordOnly ? "Chỉ từ khóa (AI chưa khả dụng)" : "Keyword + AI");
            Text(8, scored ? missingSkills.GetValueOrDefault(r.EvaluationId, "—") : "Chưa có kết quả kỹ năng");
            Text(9, ApplicationStages.IsValid(r.Stage) ? ApplicationStages.DisplayName(r.Stage) : "Không xác định");
            Text(10, RejectReasons.IsValid(r.RejectReason) ? RejectReasons.DisplayName(r.RejectReason) : "");
        }
        var table = sheet.Range(headerRow, 1, headerRow + rows.Count, Headers.Length).CreateTable("Ranking");
        table.Theme = XLTableTheme.TableStyleMedium2;
        sheet.Columns(1, Headers.Length).Width = 19;
        sheet.Column(1).Width = 8; sheet.Column(2).Width = 32; sheet.Column(8).Width = 42;
        sheet.Range(headerRow, 1, headerRow + rows.Count, Headers.Length).Style.Alignment.WrapText = true;
        sheet.Range(headerRow + 1, 3, headerRow + rows.Count, 5).Style.NumberFormat.Format = "0.0";
        sheet.SheetView.FreezeRows(headerRow);
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        using var stream = new MemoryStream(); book.SaveAs(stream); return stream.ToArray();
    }

    // Assign text values, never formulas or hyperlinks. Bound hostile/legacy values to Excel's cell limit.
    private static string SafeText(string? text)
    {
        var safe = new StringBuilder();
        foreach (var rune in (text ?? "").EnumerateRunes())
        {
            if (rune.Value < 32 && rune.Value is not (9 or 10 or 13)) continue;
            if (rune.Value is 0xFFFE or 0xFFFF) continue;
            if (safe.Length + rune.Utf16SequenceLength > 32767) break;
            safe.Append(rune.ToString());
        }
        return safe.ToString();
    }
    public static string FileName(int runId, int jobId, DateTime nowUtc) =>
        $"xep-hang-luot-{runId}-vi-tri-{jobId}-{nowUtc.AddHours(7):yyyyMMdd-HHmmss}.xlsx";
}
