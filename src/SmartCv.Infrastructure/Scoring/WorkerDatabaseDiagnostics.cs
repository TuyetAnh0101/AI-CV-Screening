using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Scoring;

/// <summary>
/// Chẩn đoán database cho tiến trình chấm điểm (A11). Mỗi vòng chấm cần HAI kết nối SQL cùng lúc:
/// một kết nối giữ khóa <c>sp_getapplock</c> suốt vòng, một kết nối của DbContext xử lý từng lượt.
/// Database ở chế độ SINGLE_USER (thường do kế thừa từ database hệ thống <c>model</c>) không cho kết nối thứ hai,
/// nên mọi lượt đứng mãi ở Queued mà Gemini chưa hề được gọi. Lớp này chỉ ĐỌC, không bao giờ đổi cấu hình database.
/// Không đọc / ghi connection string: chỉ dùng tên database.
/// </summary>
public static class WorkerDatabaseDiagnostics
{
    /// <summary>Lỗi SQL Server khi KHÔNG MỞ ĐƯỢC database (khác lỗi câu lệnh):
    /// 4060 không mở được database của login, 924 database đang SINGLE_USER và đã có người dùng,
    /// 4064 không mở được database mặc định, 18456 đăng nhập thất bại, 18452 login không tin cậy.</summary>
    private static readonly HashSet<int> DatabaseAccessErrors = [4060, 924, 4064, 18456, 18452];

    public static bool IsDatabaseAccessErrorNumber(int number) => DatabaseAccessErrors.Contains(number);

    /// <summary>Tìm trong chuỗi InnerException (kể cả AggregateException) một SqlException do không mở được database.</summary>
    public static bool TryGetDatabaseAccessError(Exception ex, out int sqlNumber)
    {
        var pending = new Stack<Exception>([ex]);
        while (pending.TryPop(out var current))
        {
            if (current is SqlException sql && IsDatabaseAccessErrorNumber(sql.Number))
            {
                sqlNumber = sql.Number;
                return true;
            }
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } inner)
                pending.Push(inner);
        }
        sqlNumber = 0;
        return false;
    }

    /// <summary>Mã lỗi của SqlException đầu tiên trong chuỗi InnerException (null nếu không có), để ghi log.</summary>
    public static int? FirstSqlErrorNumber(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
            if (current is SqlException sql) return sql.Number;
        return null;
    }

    /// <summary>Tên database trong connection string (không lộ server, tài khoản, mật khẩu).</summary>
    public static string DatabaseName(SmartCvDbContext db)
    {
        try { return db.Database.GetDbConnection().Database is { Length: > 0 } name ? name : "(không rõ)"; }
        catch (InvalidOperationException) { return "(không rõ)"; }
    }

    /// <summary>
    /// <c>sys.databases.user_access_desc</c> của database đang kết nối: MULTI_USER | SINGLE_USER | RESTRICTED_USER.
    /// Ngoài SQL Server (InMemory trong test) trả null.
    /// </summary>
    public static async Task<string?> ReadUserAccessAsync(SmartCvDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return null;
        return await db.Database
            .SqlQueryRaw<string>("SELECT user_access_desc AS [Value] FROM sys.databases WHERE database_id = DB_ID()")
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Chỉ MULTI_USER cho worker mở đồng thời hai kết nối.</summary>
    public static bool SupportsWorker(string? userAccess) =>
        userAccess is null || string.Equals(userAccess, "MULTI_USER", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gợi ý khắc phục ghi kèm log (câu lệnh chạy TỪ master, không cưỡng chế ngắt kết nối).</summary>
    public static string FixHint(string database) =>
        $"Dừng API, rồi từ database master chạy: ALTER DATABASE [{database.Replace("]", "]]")}] SET MULTI_USER; " +
        "Kiểm tra thêm chế độ của database hệ thống model (database mới tạo kế thừa chế độ này).";
}
