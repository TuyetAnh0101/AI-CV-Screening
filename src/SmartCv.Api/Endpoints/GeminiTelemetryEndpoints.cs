using System.Data;
using Microsoft.Data.SqlClient;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// Called only from the Admin-only /api/admin/ops group, which enforces JWT and verified Admin role.
/// Queries a dedicated operational table. No user, CV, prompt or candidate field is selected.
/// Google project-level quota/billing are NOT available from this telemetry.
/// </summary>
public static class GeminiTelemetryEndpoints
{
    private static readonly TimeSpan Vn = TimeSpan.FromHours(7);

    public static async Task<IResult> ReadAsync(
        string? period, IConfiguration cfg, TimeProvider clock, CancellationToken ct)
    {
        var selected = string.IsNullOrWhiteSpace(period) ? "30" : period;
        if (selected is not ("7" or "14" or "30" or "90" or "all"))
            return Results.Problem(statusCode: 400, title: "Khoảng thời gian không hợp lệ",
                detail: "Chỉ hỗ trợ 7, 14, 30, 90 hoặc all.");
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(Vn).DateTime);
        var days = selected == "all" ? 0 : int.Parse(selected, System.Globalization.CultureInfo.InvariantCulture);
        var startLocal = (days > 0 ? today.AddDays(1 - days) : today).ToDateTime(TimeOnly.MinValue);
        var from = new DateTimeOffset(startLocal, Vn).UtcDateTime;
        var lower = days > 0 ? from : new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            await using var conn = new SqlConnection(cfg.GetConnectionString("SmartCvDb"));
            await conn.OpenAsync(ct);
            var summary = await SummaryAsync(conn, selected == "all", lower, now, ct);
            var models = await ModelsAsync(conn, selected == "all", lower, now, ct);
            var trend = await TrendAsync(conn, selected == "all", lower, now, ct);
            return Results.Ok(new AdminGeminiTelemetryDto(
                now, selected, summary.First, summary.Last,
                summary.Attempts, summary.Screening, summary.PromptTest, summary.Preview,
                summary.Success, summary.HttpError, summary.NetworkOrTimeout,
                summary.Cancelled, summary.WithUsage, summary.WithCost,
                summary.Prompt, summary.Candidates, summary.Thoughts, summary.Total,
                summary.Cost, selected == "all" ? "month" : "day",
                summary.Rpm, summary.Tpm, summary.Rpd, trend, models));
        }
        catch (SqlException ex) when (ex.Number is 208 or 207)
        {
            return Results.Problem(statusCode: 503, title: "Chưa có bảng GeminiInvocationLogs",
                detail: "Admin cần áp dụng migration Gemini telemetry trên đúng database rồi khởi động lại API.",
                extensions: new Dictionary<string, object?> { ["code"] = "telemetry_schema_missing" });
        }
        catch (SqlException)
        {
            return Results.Problem(statusCode: 503, title: "Thống kê Gemini tạm thời không khả dụng",
                detail: "Kiểm tra kết nối SQL và migration. Không sử dụng số liệu cũ như số liệu mới.",
                extensions: new Dictionary<string, object?> { ["code"] = "telemetry_unavailable" });
        }
    }

    private sealed record Summary(
        long Attempts, long Screening, long PromptTest, long Preview,
        long Success, long HttpError, long NetworkOrTimeout, long Cancelled,
        long WithUsage, long WithCost,
        long? Prompt, long? Candidates, long? Thoughts, long? Total, decimal? Cost,
        DateTime? First, DateTime? Last, int Rpm, long? Tpm, int Rpd);

    private static async Task<Summary> SummaryAsync(SqlConnection conn, bool all,
        DateTime from, DateTime now, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT_BIG(*),
              COUNT_BIG(CASE WHEN Source = N'Screening' THEN 1 END),
              COUNT_BIG(CASE WHEN Source = N'PromptTest' THEN 1 END),
              COUNT_BIG(CASE WHEN Source = N'Preview' THEN 1 END),
              COUNT_BIG(CASE WHEN Outcome = N'HttpOk' THEN 1 END),
              COUNT_BIG(CASE WHEN Outcome = N'HttpError' THEN 1 END),
              COUNT_BIG(CASE WHEN Outcome IN (N'NetworkError',N'Timeout') THEN 1 END),
              COUNT_BIG(CASE WHEN Outcome = N'Cancelled' THEN 1 END),
              COUNT_BIG(CASE WHEN TotalTokens IS NOT NULL THEN 1 END),
              COUNT_BIG(CASE WHEN EstimatedCostUsd IS NOT NULL THEN 1 END),
              SUM(CONVERT(bigint, PromptTokens)), SUM(CONVERT(bigint, CandidateTokens)),
              SUM(CONVERT(bigint, ThoughtsTokens)), SUM(CONVERT(bigint, TotalTokens)),
              SUM(EstimatedCostUsd), MIN(OccurredAtUtc), MAX(OccurredAtUtc),
              COUNT_BIG(CASE WHEN OccurredAtUtc >= @minute THEN 1 END),
              SUM(CASE WHEN OccurredAtUtc >= @minute THEN CONVERT(bigint,PromptTokens) ELSE NULL END),
              COUNT_BIG(CASE WHEN OccurredAtUtc >= @day THEN 1 END)
            FROM dbo.GeminiInvocationLogs
            WHERE (@all = 1 OR OccurredAtUtc >= @from) AND OccurredAtUtc <= @until
            """;
        await using var command = Command(conn, sql, all, from, now);
        command.Parameters.AddWithValue("@minute", now.AddMinutes(-1));
        command.Parameters.AddWithValue("@day", now.AddDays(-1));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Missing aggregate row.");
        return new Summary(
            Long(reader,0),Long(reader,1),Long(reader,2),Long(reader,3),
            Long(reader,4),Long(reader,5),Long(reader,6),Long(reader,7),
            Long(reader,8),Long(reader,9),MaybeLong(reader,10),MaybeLong(reader,11),
            MaybeLong(reader,12),MaybeLong(reader,13),MaybeDecimal(reader,14),
            MaybeDate(reader,15),MaybeDate(reader,16),
            checked((int)Long(reader,17)),MaybeLong(reader,18),checked((int)Long(reader,19)));
    }

    private static async Task<IReadOnlyList<AdminGeminiTelemetryModelDto>> ModelsAsync(
        SqlConnection conn, bool all, DateTime from, DateTime now, CancellationToken ct)
    {
        const string sql = """
            SELECT ModelName, COUNT_BIG(*),
              COUNT_BIG(CASE WHEN Outcome = N'HttpOk' THEN 1 END),
              COUNT_BIG(CASE WHEN Outcome NOT IN (N'HttpOk', N'Cancelled') THEN 1 END),
              COUNT_BIG(CASE WHEN TotalTokens IS NOT NULL THEN 1 END),
              SUM(CONVERT(bigint,PromptTokens)), SUM(CONVERT(bigint,CandidateTokens)),
              SUM(CONVERT(bigint,ThoughtsTokens)), SUM(CONVERT(bigint,TotalTokens)),
              SUM(EstimatedCostUsd)
            FROM dbo.GeminiInvocationLogs
            WHERE (@all = 1 OR OccurredAtUtc >= @from) AND OccurredAtUtc <= @until
            GROUP BY ModelName ORDER BY COUNT_BIG(*) DESC, ModelName
            """;
        await using var cmd = Command(conn,sql,all,from,now);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<AdminGeminiTelemetryModelDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0),Long(reader,1),Long(reader,2),Long(reader,3),
                Long(reader,4),MaybeLong(reader,5),MaybeLong(reader,6),MaybeLong(reader,7),
                MaybeLong(reader,8),MaybeDecimal(reader,9)));
        return result;
    }

    private static async Task<IReadOnlyList<AdminGeminiTelemetryDayDto>> TrendAsync(
        SqlConnection conn, bool all, DateTime from, DateTime now, CancellationToken ct)
    {
        const string sql = """
            SELECT CASE WHEN @all = 1
              THEN DATEFROMPARTS(YEAR(DATEADD(hour,7,OccurredAtUtc)),MONTH(DATEADD(hour,7,OccurredAtUtc)),1)
              ELSE CONVERT(date,DATEADD(hour,7,OccurredAtUtc)) END,
              COUNT_BIG(*),
              COUNT_BIG(CASE WHEN Outcome=N'HttpOk' THEN 1 END),
              COUNT_BIG(CASE WHEN Outcome NOT IN (N'HttpOk',N'Cancelled') THEN 1 END),
              SUM(CONVERT(bigint,PromptTokens)),SUM(CONVERT(bigint,CandidateTokens)),
              SUM(CONVERT(bigint,TotalTokens)),SUM(EstimatedCostUsd)
            FROM dbo.GeminiInvocationLogs
            WHERE (@all = 1 OR OccurredAtUtc >= @from) AND OccurredAtUtc <= @until
            GROUP BY CASE WHEN @all = 1
              THEN DATEFROMPARTS(YEAR(DATEADD(hour,7,OccurredAtUtc)),MONTH(DATEADD(hour,7,OccurredAtUtc)),1)
              ELSE CONVERT(date,DATEADD(hour,7,OccurredAtUtc)) END
            ORDER BY 1
            """;
        await using var cmd = Command(conn,sql,all,from,now);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<AdminGeminiTelemetryDayDto>();
        while (await reader.ReadAsync(ct))
            rows.Add(new(DateOnly.FromDateTime(reader.GetDateTime(0)),Long(reader,1),Long(reader,2),Long(reader,3),
                MaybeLong(reader,4),MaybeLong(reader,5),MaybeLong(reader,6),MaybeDecimal(reader,7)));
        return rows;
    }

    private static SqlCommand Command(SqlConnection conn, string sql, bool all, DateTime from, DateTime now)
    {
        var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        cmd.Parameters.Add("@all", SqlDbType.Bit).Value = all;
        cmd.Parameters.Add("@from", SqlDbType.DateTime2).Value = from;
        cmd.Parameters.Add("@until", SqlDbType.DateTime2).Value = now;
        return cmd;
    }
    private static long Long(SqlDataReader r, int index) => r.GetInt64(index);
    private static long? MaybeLong(SqlDataReader r, int index) => r.IsDBNull(index) ? null : r.GetInt64(index);
    private static decimal? MaybeDecimal(SqlDataReader r, int index) => r.IsDBNull(index) ? null : r.GetDecimal(index);
    private static DateTime? MaybeDate(SqlDataReader r, int index) => r.IsDBNull(index) ? null : r.GetDateTime(index);
}
