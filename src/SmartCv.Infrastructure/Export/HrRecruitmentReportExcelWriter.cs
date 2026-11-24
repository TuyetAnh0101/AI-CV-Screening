using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using SmartCv.Core.Contracts;

namespace SmartCv.Infrastructure.Export;

/// <summary>Separate from ranking Excel; emits a recruitment cohort report with one line per application.</summary>
public static class HrRecruitmentReportExcelWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Write(HrReportSummaryDto summary, IReadOnlyList<HrReportRowDto> rows,
        string scope, bool anonymous, DateTime utcNow)
    {
        using var book = new XLWorkbook();
        book.Properties.Author = "SCApp";
        var overview = book.Worksheets.Add("Tong quan");
        overview.Cell(1, 1).Value = "SCApp · Báo cáo tuyển dụng tổng hợp";
        overview.Cell(1, 1).Style.Font.SetBold();
        overview.Cell(1, 1).Style.Font.FontSize = 16;
        overview.Cell(2, 1).Value = Safe(scope);
        overview.Cell(3, 1).Value = $"Xuất lúc {utcNow.AddHours(7):dd/MM/yyyy HH:mm} UTC+7; ẩn danh: {(anonymous ? "Có" : "Không")}";
        overview.Cell(4, 1).Value = "Kết quả AI hỗ trợ, quyết định tuyển dụng thuộc HR. Không bao gồm PromptTest.";
        var metrics = new (string Label, object Value)[]
        {
            ("Lượt ứng tuyển", summary.Applications),
            ("CV duy nhất trong tập lọc", summary.UniqueCvs),
            ("Ứng viên đã xác định (CandidateId duy nhất)", summary.KnownUniqueCandidates),
            ("Lượt ứng tuyển chưa liên kết Candidate", summary.ApplicationsWithoutCandidate),
            ("Hồ sơ có điểm Screening", summary.EvaluatedApplications),
            ("Combined", summary.AiCombined),
            ("KeywordOnly", summary.KeywordOnly),
            ("Không có kết quả hoàn tất", summary.NoCompletedEvaluation),
            ("Đạt ngưỡng điểm của lượt chấm", summary.PassedScore),
            ("Dưới ngưỡng điểm", summary.BelowThreshold),
            ("Từng qua vòng CV (lịch sử HR)", summary.EverPassedCvRound),
            ("Tỷ lệ từng qua vòng CV (%)", summary.HrPassedPercent),
            ("Tỷ lệ đang ở giai đoạn loại (%)", summary.CurrentRejectedPercent),
            ("Hồ sơ cần xử lý", summary.NeedsAction),
            ("Có lịch sử quyết định đầu tiên", summary.FirstDecisionSampleCount)
        };
        for (var i = 0; i < metrics.Length; i++)
        {
            overview.Cell(i + 6, 1).Value = metrics[i].Label;
            switch (metrics[i].Value)
            {
                case int n: overview.Cell(i + 6, 2).Value = n; break;
                case decimal n: overview.Cell(i + 6, 2).Value = (double)n; break;
            }
        }
        overview.Cell(22, 1).Value = "Điểm trung bình (hồ sơ có điểm)";
        if (summary.AverageFinalScore is decimal score) overview.Cell(22, 2).Value = (double)score;
        overview.Cell(23, 1).Value = "Thời gian TB đến quyết định HR đầu tiên (giờ)";
        if (summary.AverageHoursToFirstDecision is decimal hours) overview.Cell(23, 2).Value = (double)hours;
        overview.Column(1).Width = 55; overview.Column(2).Width = 22;

        var details = book.Worksheets.Add("Ho so");
        string[] headers = ["Mã hồ sơ", "Ứng viên / mã ẩn danh", "Job", "Ngày ứng tuyển UTC+7", "Giai đoạn HR",
            "Lý do loại", "Run Screening", "Điểm cuối", "Ngưỡng", "Qua ngưỡng", "Nguồn điểm", "Mức phù hợp",
            "Đã từng qua vòng CV", "Cần xử lý"];
        for (var col = 0; col < headers.Length; col++) details.Cell(1, col + 1).Value = headers[col];
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var line = i + 2;
            details.Cell(line, 1).Value = r.ApplicationId;
            details.Cell(line, 2).Value = Safe(r.Applicant);
            details.Cell(line, 3).Value = Safe(r.JobTitle);
            details.Cell(line, 4).Value = r.AppliedAtUtc.AddHours(7).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
            details.Cell(line, 5).Value = Safe(r.StageText);
            details.Cell(line, 6).Value = Safe(r.RejectReasonText);
            if (r.RunId is int run) details.Cell(line, 7).Value = run;
            if (r.FinalScore is decimal scoreValue) details.Cell(line, 8).Value = (double)scoreValue;
            if (r.PassThreshold is decimal threshold) details.Cell(line, 9).Value = (double)threshold;
            details.Cell(line, 10).Value = r.PassedScore switch { true => "Đạt", false => "Không đạt", _ => "Chưa có điểm" };
            details.Cell(line, 11).Value = Safe(r.ScoreSource);
            details.Cell(line, 12).Value = Safe(r.FitLabel);
            details.Cell(line, 13).Value = r.EverPassedCvRound ? "Có" : "Chưa";
            details.Cell(line, 14).Value = r.NeedsAction ? "Có" : "Không";
        }
        var range = details.Range(1, 1, Math.Max(2, rows.Count + 1), headers.Length);
        range.Style.Alignment.WrapText = true;
        details.Row(1).Style.Font.SetBold();
        details.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#133d46");
        details.Row(1).Style.Font.FontColor = XLColor.White;
        details.Columns(1, headers.Length).Width = 19;
        details.Column(2).Width = 32;
        details.Column(3).Width = 32;
        details.SheetView.FreezeRows(1);

        var stages = book.Worksheets.Add("Giai doan va ly do");
        stages.Cell(1, 1).Value = "Giai đoạn hiện tại"; stages.Cell(1, 2).Value = "Số hồ sơ";
        for (var i = 0; i < summary.Stages.Count; i++)
        {
            stages.Cell(i + 2, 1).Value = Safe(summary.Stages[i].Label);
            stages.Cell(i + 2, 2).Value = summary.Stages[i].Count;
        }
        stages.Cell(10, 1).Value = "Lý do loại (hiện tại)"; stages.Cell(10, 2).Value = "Số hồ sơ";
        for (var i = 0; i < summary.RejectReasons.Count; i++)
        {
            stages.Cell(i + 11, 1).Value = Safe(summary.RejectReasons[i].Label);
            stages.Cell(i + 11, 2).Value = summary.RejectReasons[i].Count;
        }
        stages.Columns(1, 2).Width = 39;
        using var ms = new MemoryStream();
        book.SaveAs(ms);
        return ms.ToArray();
    }

    private static string Safe(string? value)
    {
        var buffer = new StringBuilder();
        foreach (var rune in (value ?? "").EnumerateRunes())
        {
            if (rune.Value < 32 && rune.Value is not (9 or 10 or 13)) continue;
            if (buffer.Length + rune.Utf16SequenceLength > 32_700) break;
            buffer.Append(rune.ToString());
        }
        var text = buffer.ToString();
        // Never permit a user-sourced label/name to become a spreadsheet formula.
        // ClosedXML consumes the FIRST leading apostrophe as Excel's quote prefix.
        // Two leading apostrophes keep one literal apostrophe in the saved cell value.
        // This prevents the resulting cell from starting with =, +, -, @, tab or CR
        // even after saving/reopening the .xlsx and prevents formula interpretation.
        return text.Length > 0 && "=+-@\t\r".Contains(text[0]) ? "''" + text : text;
    }

    public static string Filename(DateTime nowUtc) => $"scapp-bao-cao-tuyen-dung-{nowUtc.AddHours(7):yyyyMMdd-HHmmss}.xlsx";
}
