using System.Globalization;
using ClosedXML.Excel;
using SmartCv.Core.Backup;

namespace SmartCv.Infrastructure.Export;

/// <summary>Backup operations workbook: job metadata only. No paths, secrets, file ids or applicant data.</summary>
public static class BackupReportExcelWriter
{
    public static byte[] Write(BackupReportDto report)
    {
        using var book = new XLWorkbook();
        var summary = book.Worksheets.Add("Tổng quan");
        var row = 1;
        void Line(string label, object? value)
        {
            summary.Cell(row, 1).Value = label;
            var cell = summary.Cell(row, 2);
            switch (value)
            {
                case null: cell.Value = "Chưa ghi nhận"; break;
                case int number: cell.Value = number; break;
                case long number: cell.Value = number; break;
                case double number: cell.Value = number; break;
                case DateTime time: cell.Value = Time(time); break;
                default: cell.Value = value.ToString(); break;
            }
            row++;
        }
        Line("Báo cáo sao lưu SCApp (Google Drive)", $"{report.From:dd/MM/yyyy} – {report.To:dd/MM/yyyy} (giờ Việt Nam)");
        Line("Tạo lúc", report.GeneratedAtUtc);
        Line("Tổng số công việc", report.Total);
        Line("Thành công", report.Succeeded);
        Line("Thất bại", report.Failed);
        Line("Bị lỡ lịch", report.Missed);
        Line("Tỷ lệ thành công (%)", report.SuccessRate);
        Line("Bản đầy đủ mới nhất", report.LastSuccessAtUtc);
        Line("Lần chạy dự kiến tiếp theo", report.NextFullAtUtc);
        Line("Kiểm tra toàn vẹn gần nhất", report.LastVerifyAtUtc);
        Line("Khôi phục thử gần nhất", report.LastRestoreDrillAtUtc);
        Line("Kết quả khôi phục thử", report.LastRestoreDrillStatus is null ? null : BackupLabels.Status(report.LastRestoreDrillStatus));
        Line("Tổng dung lượng trên Drive (MB)", Megabytes(report.StoredBytes));
        Line("Database bản mới nhất (MB)", report.LastDatabaseBytes is { } d ? Megabytes(d) : null);
        Line("Tệp CV bản mới nhất (MB)", report.LastCvBytes is { } v ? Megabytes(v) : null);
        Line("Đã tải lên trong kỳ (MB)", Megabytes(report.UploadedBytes));
        Line("CV mới trong kỳ (MB)", Megabytes(report.NewCvBytes));
        Line("Điểm khôi phục đang giữ", report.RestorePoints);
        row++;
        Line("Cảnh báo chưa xử lý", report.OpenWarnings.Count);
        foreach (var warning in report.OpenWarnings) Line("", warning.Message);
        summary.Column(1).Width = 36; summary.Column(2).Width = 90; summary.Cell(1, 1).Style.Font.Bold = true;

        var history = book.Worksheets.Add("Lịch sử");
        string[] headers = ["Yêu cầu", "Kết thúc", "Loại", "Cách chạy", "Kết quả", "Mã lỗi", "Database (MB)", "Tải lên (MB)", "Tệp CV", "CV mới", "CV mới (MB)"];
        for (var column = 0; column < headers.Length; column++) history.Cell(1, column + 1).Value = headers[column];
        var line = 2;
        foreach (var job in report.Jobs)
        {
            history.Cell(line, 1).Value = Time(job.RequestedAtUtc);
            history.Cell(line, 2).Value = job.FinishedAtUtc is { } finished && job.Status != BackupJobStatuses.Missed ? Time(finished) : "";
            history.Cell(line, 3).Value = BackupLabels.Type(job.Type);
            history.Cell(line, 4).Value = BackupLabels.Trigger(job.Trigger);
            history.Cell(line, 5).Value = BackupLabels.Status(job.Status);
            history.Cell(line, 6).Value = job.ErrorCode ?? "";
            if (job.DatabaseBytes is { } database) history.Cell(line, 7).Value = Megabytes(database);
            if (job.UploadedBytes is { } uploaded) history.Cell(line, 8).Value = Megabytes(uploaded);
            if (job.CvFiles is { } files) history.Cell(line, 9).Value = files;
            if (job.NewCvFiles is { } added) history.Cell(line, 10).Value = added;
            if (job.NewCvBytes is { } addedBytes) history.Cell(line, 11).Value = Megabytes(addedBytes);
            line++;
        }
        history.Row(1).Style.Font.Bold = true; history.SheetView.FreezeRows(1); history.Columns(1, headers.Length).Width = 18;

        var weeks = book.Worksheets.Add("Theo tuần");
        weeks.Cell(1, 1).Value = "Tuần bắt đầu"; weeks.Cell(1, 2).Value = "Thành công"; weeks.Cell(1, 3).Value = "Thất bại"; weeks.Cell(1, 4).Value = "Lỡ lịch";
        var week = 2;
        foreach (var item in report.Weeks)
        {
            weeks.Cell(week, 1).Value = item.WeekStart.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            weeks.Cell(week, 2).Value = item.Succeeded; weeks.Cell(week, 3).Value = item.Failed; weeks.Cell(week, 4).Value = item.Missed; week++;
        }
        weeks.Row(1).Style.Font.Bold = true; weeks.Columns(1, 4).Width = 18;
        using var stream = new MemoryStream(); book.SaveAs(stream); return stream.ToArray();
    }

    private static double Megabytes(long bytes) => Math.Round(bytes / 1048576.0, 2);
    private static string Time(DateTime utc) => utc.AddHours(7).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
}
