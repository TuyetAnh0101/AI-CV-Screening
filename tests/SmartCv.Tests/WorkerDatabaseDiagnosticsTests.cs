using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

/// <summary>A11: phân loại lỗi "không mở được database" và gợi ý khắc phục (không cần SQL Server).</summary>
public class WorkerDatabaseDiagnosticsTests
{
    [Theory]
    [InlineData(4060, true)]    // không mở được database (vd. SINGLE_USER đang có người dùng)
    [InlineData(924, true)]     // database đã mở và chỉ cho một người dùng
    [InlineData(18456, true)]   // đăng nhập thất bại
    [InlineData(1205, false)]   // deadlock: lỗi câu lệnh, không phải lỗi truy cập database
    [InlineData(547, false)]    // vi phạm khóa ngoại
    [InlineData(0, false)]
    public void Only_database_access_errors_are_classified(int number, bool expected) =>
        Assert.Equal(expected, WorkerDatabaseDiagnostics.IsDatabaseAccessErrorNumber(number));

    [Fact]
    public void Non_sql_exceptions_are_not_database_access_errors()
    {
        var ex = new InvalidOperationException("Có lượt chấm xử lý lỗi",
            new AggregateException(new TimeoutException(), new HttpRequestException("Gemini")));

        Assert.False(WorkerDatabaseDiagnostics.TryGetDatabaseAccessError(ex, out var number));
        Assert.Equal(0, number);
    }

    [Theory]
    [InlineData("MULTI_USER", true)]
    [InlineData("multi_user", true)]
    [InlineData("SINGLE_USER", false)]
    [InlineData("RESTRICTED_USER", false)]
    [InlineData(null, true)]   // ngoài SQL Server (InMemory): không áp dụng
    public void Only_multi_user_database_supports_the_worker(string? access, bool expected) =>
        Assert.Equal(expected, WorkerDatabaseDiagnostics.SupportsWorker(access));

    [Fact]
    public void Fix_hint_names_only_the_database_and_runs_from_master_without_forcing_disconnects()
    {
        var hint = WorkerDatabaseDiagnostics.FixHint("SmartCvDb");

        Assert.Contains("ALTER DATABASE [SmartCvDb] SET MULTI_USER", hint);
        Assert.Contains("master", hint);
        Assert.Contains("model", hint);
        Assert.DoesNotContain("ROLLBACK IMMEDIATE", hint);
        Assert.Contains("ALTER DATABASE [a]]b] SET MULTI_USER", WorkerDatabaseDiagnostics.FixHint("a]b"));   // tên có ] vẫn an toàn
    }
}
