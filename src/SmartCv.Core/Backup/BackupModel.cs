using System.Text.RegularExpressions;

namespace SmartCv.Core.Backup;

public static class BackupJobTypes
{
    public const string Full = "full";
    public const string Log = "log";
    public const string Verify = "verify";
    public const string RestoreDrill = "restore-drill";
    public const string Recovery = "recovery";
    public const string CatalogSync = "catalog-sync";
    public static IReadOnlyList<string> All { get; } = [Full, Log, Verify, RestoreDrill, Recovery, CatalogSync];
    /// <summary>Only the restore worker (separate account with CREATE DATABASE) runs these.</summary>
    public static IReadOnlyList<string> RestoreRole { get; } = [RestoreDrill, Recovery];
    public static IReadOnlyList<string> BackupRole { get; } = [Full, Log, Verify, CatalogSync];
}

public static class BackupJobStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Missed = "Missed";
    public const string Cancelled = "Cancelled";
    public static IReadOnlyList<string> All { get; } = [Queued, Running, Succeeded, Failed, Missed, Cancelled];
}

public static class BackupTriggers
{
    public const string Scheduled = "scheduled";
    public const string CatchUp = "catch-up";
    public const string Retry = "retry";
    public const string Manual = "manual";
    public static IReadOnlyList<string> All { get; } = [Scheduled, CatchUp, Retry, Manual];
}

public static class BackupPointKinds { public const string Full = "Full"; public const string Log = "Log"; }
public static class BackupPointStatuses { public const string Available = "Available"; public const string Deleted = "Deleted"; public const string Damaged = "Damaged"; }
public static class BackupWorkerRoles { public const string Backup = "backup"; public const string Restore = "restore"; }

/// <summary>Stable error codes. Only these leave the worker; never exception text, paths or tokens.</summary>
public static class BackupErrors
{
    public const string Config = "A62_CONFIG";
    public const string Key = "A62_KEY";
    public const string KeyMismatch = "A62_KEY_MISMATCH";
    public const string DriveNotConnected = "A62_DRIVE_NOT_CONNECTED";
    public const string DriveAuth = "A62_DRIVE_AUTH";
    public const string DriveQuota = "A62_DRIVE_QUOTA";
    public const string DriveUnavailable = "A62_DRIVE_UNAVAILABLE";
    public const string DriveMissing = "A62_DRIVE_MISSING";
    public const string DriveVerify = "A62_DRIVE_VERIFY";
    public const string Space = "A62_SPACE";
    public const string Locked = "A62_LOCKED";
    public const string SqlConnect = "A62_SQL_CONNECT";
    public const string SqlBackup = "A62_SQL_BACKUP";
    public const string Database = "A62_DATABASE";
    public const string Files = "A62_FILES";
    public const string Keys = "A62_KEYS";
    public const string Consistency = "A62_CONSISTENCY";
    public const string Checksum = "A62_CHECKSUM";
    public const string Dependency = "A62_DEPENDENCY";
    public const string LogChain = "A62_LOG_CHAIN";
    public const string RecoveryModel = "A62_RECOVERY_MODEL";
    public const string Restore = "A62_RESTORE";
    public const string Compare = "A62_COMPARE";
    public const string Timeout = "A62_TIMEOUT";
    public const string Interrupted = "A62_INTERRUPTED";
    public const string Missed = "A62_MISSED";
    public const string Unexpected = "A62_UNEXPECTED";
    /// <summary>There is no restore point yet for a job that needs one.</summary>
    public const string NoBackup = "A62_NO_BACKUP";
    /// <summary>The service account lacks a permission its role needs.</summary>
    public const string Privilege = "A62_PRIVILEGE";
    /// <summary>Warning only: the backup account can do more than its role needs.</summary>
    public const string ExcessPrivilege = "A62_EXCESS_PRIVILEGE";
    private static readonly Regex Shape = new("^A62_[A-Z_]{1,40}$", RegexOptions.CultureInvariant);
    public static bool IsValid(string? code) => code is not null && Shape.IsMatch(code);

    /// <summary>Failures that can clear by themselves; scheduled fulls retry these at most twice.</summary>
    public static bool IsTransient(string? code) => code is Locked or SqlConnect or SqlBackup or Consistency or
        DriveUnavailable or Timeout or Interrupted;
}

/// <summary>Exception carrying only a stable code.</summary>
public sealed class BackupException(string code) : Exception(code)
{
    public string Code { get; } = BackupErrors.IsValid(code) ? code : BackupErrors.Unexpected;
}

public static class BackupLimits
{
    public const int MinRetention = 7, MaxRetention = 90;
    public const int MinLogHours = 1, MaxLogHours = 24;
    private static readonly Regex Time = new("^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant);
    public static bool IsValidTime(string? value) => value is not null && Time.IsMatch(value);
    /// <summary>Restored databases always get a fresh name in this exact shape: never an existing database.</summary>
    public static readonly Regex RestoreDatabase = new("^SmartCv_Restore_[a-f0-9]{32}$", RegexOptions.CultureInvariant);
}

/// <summary>Vietnamese names of the fixed job vocabulary, shared by the page and the workbook.</summary>
public static class BackupLabels
{
    public static string Type(string type) => type switch
    {
        BackupJobTypes.Full => "Sao lưu đầy đủ", BackupJobTypes.Log => "Transaction log", BackupJobTypes.Verify => "Kiểm tra toàn vẹn",
        BackupJobTypes.RestoreDrill => "Khôi phục thử", BackupJobTypes.Recovery => "Khôi phục có kiểm soát",
        BackupJobTypes.CatalogSync => "Đồng bộ danh mục từ Drive", _ => "Khác"
    };
    public static string Trigger(string trigger) => trigger switch
    { BackupTriggers.Scheduled => "Theo lịch", BackupTriggers.CatchUp => "Chạy bù", BackupTriggers.Retry => "Thử lại tự động", BackupTriggers.Manual => "Admin yêu cầu", _ => "Khác" };
    public static string Status(string status) => status switch
    {
        BackupJobStatuses.Queued => "Đang chờ", BackupJobStatuses.Running => "Đang chạy", BackupJobStatuses.Succeeded => "Thành công",
        BackupJobStatuses.Failed => "Thất bại", BackupJobStatuses.Missed => "Bị lỡ", BackupJobStatuses.Cancelled => "Đã hủy", _ => "Khác"
    };
    public static string Error(string? code) => code switch
    {
        null => "",
        BackupErrors.DriveNotConnected => "Chưa kết nối Google Drive.",
        BackupErrors.DriveAuth => "Quyền truy cập Google Drive đã hết hiệu lực hoặc bị thu hồi. Kết nối lại Drive.",
        BackupErrors.DriveQuota => "Google Drive đã hết dung lượng.",
        BackupErrors.DriveUnavailable => "Không kết nối được Google Drive (mạng hoặc dịch vụ). Hệ thống sẽ thử lại.",
        BackupErrors.DriveMissing => "Tệp sao lưu trên Drive đã bị xóa hoặc không truy cập được.",
        BackupErrors.DriveVerify => "Drive báo kích thước/checksum khác tệp đã gửi.",
        BackupErrors.Key => "Thiếu khóa mã hóa sao lưu trên máy chủ.",
        BackupErrors.KeyMismatch => "Bản sao lưu được mã hóa bằng khóa khác khóa hiện tại.",
        BackupErrors.Space => "Không đủ dung lượng staging tạm (kèm phần dự phòng).",
        BackupErrors.Locked => "Đang có thao tác xóa/ẩn danh CV hoặc một lần sao lưu khác.",
        BackupErrors.SqlConnect => "Không kết nối được SQL Server.",
        BackupErrors.SqlBackup => "SQL Server không tạo được bản sao lưu.",
        BackupErrors.Files => "Thiếu hoặc sai tệp CV mà cơ sở dữ liệu đang tham chiếu.",
        BackupErrors.Keys => "Không đọc được key ring Data Protection.",
        BackupErrors.Consistency => "Dữ liệu CV thay đổi bất thường trong lúc sao lưu.",
        BackupErrors.Checksum => "Dữ liệu tải về không khớp checksum.",
        BackupErrors.Dependency => "Thiếu tệp CV mà điểm khôi phục cần.",
        BackupErrors.LogChain => "Chuỗi transaction log bị đứt; cần một bản sao lưu đầy đủ mới.",
        BackupErrors.RecoveryModel => "Database ở SIMPLE recovery, không có transaction log để sao lưu.",
        BackupErrors.Restore => "SQL Server không khôi phục được bản sao lưu.",
        BackupErrors.Compare => "Dữ liệu khôi phục không khớp bản sao lưu.",
        BackupErrors.Timeout => "Quá thời gian cho phép.",
        BackupErrors.Interrupted => "Worker bị dừng giữa chừng.",
        BackupErrors.Missed => "Máy không chạy đúng giờ đã hẹn.",
        BackupErrors.NoBackup => "Chưa có bản sao lưu đầy đủ thành công để làm việc này.",
        BackupErrors.Privilege => "Tài khoản dịch vụ thiếu quyền cần thiết.",
        BackupErrors.ExcessPrivilege => "Tài khoản dịch vụ đang có nhiều quyền hơn cần thiết.",
        _ => "Xem nhật ký của worker."
    };

    /// <summary>What a person has to do when waiting or retrying will not help.</summary>
    public static string Guidance(string? code) => code switch
    {
        BackupErrors.Files => "Chạy lệnh cv-report trên máy chủ, đưa tệp CV về đúng chỗ rồi chạy Sao lưu ngay.",
        BackupErrors.DriveAuth or BackupErrors.DriveNotConnected => "Mở tab Google Drive và kết nối lại.",
        BackupErrors.DriveQuota => "Giải phóng hoặc nâng dung lượng Google Drive.",
        BackupErrors.DriveMissing or BackupErrors.DriveVerify or BackupErrors.Checksum => "Khôi phục tệp trong thùng rác Drive nếu có, rồi tạo bản đầy đủ mới.",
        BackupErrors.Key or BackupErrors.KeyMismatch => "Nhập đúng khóa khôi phục bằng lệnh init-key trên máy chủ.",
        BackupErrors.Space => "Giải phóng dung lượng ổ đĩa chứa thư mục tạm.",
        BackupErrors.Keys => "Cấp quyền đọc key ring Data Protection cho tài khoản dịch vụ.",
        BackupErrors.Privilege => "Chạy lại trình cài đặt worker để cấp đúng quyền SQL, rồi chạy lệnh check.",
        BackupErrors.LogChain or BackupErrors.NoBackup => "Chạy một bản sao lưu đầy đủ mới.",
        BackupErrors.RecoveryModel => "Quản trị viên SQL quyết định đổi recovery model sang FULL.",
        BackupErrors.Dependency or BackupErrors.Compare or BackupErrors.Restore or BackupErrors.Database => "Xem nhật ký worker; chạy Kiểm tra toàn vẹn trước khi khôi phục lại.",
        BackupErrors.Config => "Kiểm tra tệp cấu hình worker bằng lệnh check.",
        _ => "Xem nhật ký của worker trên máy chủ."
    };
}

/// <summary>Pure schedule rules. Times are the server's local time zone.</summary>
public static class BackupSchedule
{
    public static DateTime SlotAtOrBefore(DateTime local, string fullTime)
    {
        var slot = local.Date + TimeSpan.Parse(fullTime, System.Globalization.CultureInfo.InvariantCulture);
        return slot > local ? slot.AddDays(-1) : slot;
    }

    public sealed record FullPlan(IReadOnlyList<DateTime> MissedLocal, DateTime? RunSlotLocal, bool CatchUp, DateTime HandledLocal);

    /// <summary>
    /// Due slots since the last handled one. Each slot older than the grace period that was not run is recorded as
    /// missed; at most one backup (the newest due slot) runs. The first call never invents missed history.
    /// </summary>
    public static FullPlan Plan(string fullTime, bool paused, DateTime? lastHandledLocal, DateTime nowLocal, TimeSpan grace)
    {
        var current = SlotAtOrBefore(nowLocal, fullTime);
        if (lastHandledLocal is null || paused) return new([], null, false, current);
        var due = new List<DateTime>();
        for (var slot = current; slot > lastHandledLocal && due.Count < 14; slot = slot.AddDays(-1)) due.Insert(0, slot);
        if (due.Count == 0) return new([], null, false, lastHandledLocal.Value > current ? lastHandledLocal.Value : current);
        var newest = due[^1];
        var late = nowLocal - newest > grace;
        var missed = due.Take(due.Count - 1).ToList();
        if (late) missed.Add(newest);
        return new(missed, newest, late, current);
    }

    public static DateTime NextFull(string fullTime, DateTime nowLocal) => SlotAtOrBefore(nowLocal, fullTime).AddDays(1);
}
