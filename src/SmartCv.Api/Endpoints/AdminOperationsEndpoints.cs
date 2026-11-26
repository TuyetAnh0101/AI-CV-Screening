using System.Text.Json;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SmartCv.Api.Auth;
using SmartCv.Api.Auditing;
using SmartCv.Api.Ops;
using SmartCv.Core.Contracts;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// Dedicated ADMIN-only operational endpoints. Business API route groups do not enclose
/// this group. Counts use tightly bounded read-only SQL, never candidate data/metadata.
/// This intentional cross-workspace read is separate from the normal HR EF query scope.
/// </summary>
public static class AdminOperationsEndpoints
{
    private static readonly TimeSpan VnOffset = TimeSpan.FromHours(7);

    public static IEndpointRouteBuilder MapAdminOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/ops").WithTags("Admin Operations")
            .RequireAuthorization(AuthPolicies.Admin)
            .AddEndpointFilter(async (ctx, next) =>
            {
                ctx.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(ctx);
            });
        group.MapGet("/overview", OverviewAsync).WithName("AdminOpsOverview");
        group.MapGet("/ai-usage", AiUsageAsync).WithName("AdminOpsAiUsage");
        group.MapGet("/ai-history", AiHistoryAsync).WithName("AdminOpsAiHistory");
        group.MapGet("/gemini-telemetry", GeminiTelemetryEndpoints.ReadAsync)
            .WithName("AdminOpsGeminiTelemetry");
        group.MapGet("/workspaces", WorkspacesAsync).WithName("AdminOpsWorkspaces");
        group.MapPatch("/workspaces/{id:int}/quota", ChangeQuotaAsync)
            .WithName("AdminOpsChangeWorkspaceQuota");
        group.MapGet("/integrations", IntegrationsAsync).WithName("AdminOpsIntegrations");
        group.MapGet("/integrations/insights", ProviderInsightsAsync)
            .WithName("AdminOpsProviderInsights");
        group.MapPost("/integrations/gemini/check", CheckGeminiAsync)
            .RequireRateLimiting(AuthSetup.ExternalStartRateLimit)
            .WithName("AdminOpsGeminiCheck");
        return app;
    }

    private static (DateTime FromUtc, DateTime ToUtc, DateOnly Today) VnRange(TimeProvider clock, int days)
    {
        var local = clock.GetUtcNow().ToOffset(VnOffset);
        var today = DateOnly.FromDateTime(local.DateTime);
        var from = today.AddDays(1 - days).ToDateTime(TimeOnly.MinValue);
        var to = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return (new DateTimeOffset(from, VnOffset).UtcDateTime,
            new DateTimeOffset(to, VnOffset).UtcDateTime, today);
    }

    private static async Task<IResult> OverviewAsync(SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var (from, to, _) = VnRange(clock, 1);
        const string sql = """
            SELECT
              (SELECT COUNT_BIG(*) FROM dbo.Users) AS TotalAccounts,
              (SELECT COUNT_BIG(*) FROM dbo.Users WHERE IsActive = 1) AS ActiveAccounts,
              (SELECT COUNT_BIG(*) FROM dbo.Workspaces WHERE IsActive = 1) AS ActiveWorkspaces,
              (SELECT COUNT_BIG(*) FROM dbo.Workspaces WHERE Kind = 'Personal') AS PersonalWorkspaces,
              (SELECT COUNT_BIG(*) FROM dbo.Jobs) AS TotalJobs,
              (SELECT COUNT_BIG(*) FROM dbo.Jobs WHERE IsActive = 1) AS ActiveJobs,
              (SELECT COUNT_BIG(*) FROM dbo.Cvs WHERE AnonymizedAt IS NULL) AS StoredCvs,
              (SELECT COUNT_BIG(*) FROM dbo.Applications) AS TotalApplications,
              (SELECT COUNT_BIG(*) FROM dbo.EvaluationRuns) AS TotalRuns,
              (SELECT COUNT_BIG(*) FROM dbo.EvaluationRuns WHERE Status IN ('Queued', 'Running')) AS PendingRuns,
              (SELECT COUNT_BIG(*) FROM dbo.EvaluationRuns WHERE Status = 'Failed' AND CreatedAt >= @since) AS FailedRuns24h,
              (SELECT COALESCE(SUM(CONVERT(bigint, Units - UnitsReleased)), 0)
               FROM dbo.AiUsageEntries WHERE CreatedAtUtc >= @from AND CreatedAtUtc < @to) AS AiToday,
              (SELECT COUNT_BIG(*) FROM dbo.AuditEvents
               WHERE OccurredAtUtc >= @since AND Outcome IN ('Failed', 'Denied')) AS AuditFailures,
              (SELECT COUNT_BIG(*) FROM dbo.Workspaces) AS TotalWorkspaces,
              (SELECT COUNT_BIG(*) FROM dbo.Workspaces WHERE IsActive = 0) AS InactiveWorkspaces,
              (SELECT COUNT_BIG(*) FROM dbo.Workspaces WHERE DailyAiEvaluationLimit IS NOT NULL OR MaxCvCount IS NOT NULL) AS QuotaConfiguredWorkspaces
            """;
        var rows = await ReadAsync(db, sql,
            c =>
            {
                Add(c, "@from", from); Add(c, "@to", to); Add(c, "@since", now.AddHours(-24));
            }, reader => new AdminSystemOverviewDto(now,
                Long(reader, 0), Long(reader, 1), Long(reader, 2), Long(reader, 3),
                Long(reader, 4), Long(reader, 5), Long(reader, 6), Long(reader, 7),
                Long(reader, 8), Long(reader, 9), Long(reader, 10), Long(reader, 11), Long(reader, 12))
            {
                TotalWorkspaces = Long(reader, 13), InactiveWorkspaces = Long(reader, 14), QuotaConfiguredWorkspaces = Long(reader, 15)
            }, ct);
        return Results.Ok(rows.Single());
    }

    private static async Task<IResult> AiUsageAsync(int? days, SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var selected = days ?? 14;
        if (selected is not (7 or 14 or 30 or 90))
            return BadRequest("days chỉ nhận 7, 14, 30 hoặc 90.");
        var (from, to, today) = VnRange(clock, selected);
        const string sql = """
            SELECT CONVERT(date, DATEADD(hour, 7, CreatedAtUtc)) AS UsageDate,
              COALESCE(SUM(CASE WHEN Source = 'Screening' THEN CONVERT(bigint, Units - UnitsReleased) ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN Source = 'PromptTest' THEN CONVERT(bigint, Units - UnitsReleased) ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN Source = 'Preview' THEN CONVERT(bigint, Units - UnitsReleased) ELSE 0 END), 0)
            FROM dbo.AiUsageEntries WHERE CreatedAtUtc >= @from AND CreatedAtUtc < @to
            GROUP BY CONVERT(date, DATEADD(hour, 7, CreatedAtUtc)) ORDER BY UsageDate
            """;
        var points = await ReadAsync(db, sql, c => { Add(c, "@from", from); Add(c, "@to", to); },
            r => new AdminAiUsagePointDto(DateOnly.FromDateTime(r.GetDateTime(0)),
                Long(r, 1), Long(r, 2), Long(r, 3)), ct);
        var byDay = points.ToDictionary(p => p.Date);
        var filled = Enumerable.Range(0, selected)
            .Select(i => today.AddDays(i - selected + 1))
            .Select(date => byDay.GetValueOrDefault(date) ?? new AdminAiUsagePointDto(date, 0, 0, 0))
            .ToArray();
        // AiUsageEntries records RESERVATIONS, not actual Gemini calls or provider tokens.
        return Results.Ok(new AdminAiUsageDto(clock.GetUtcNow().UtcDateTime,
            selected, filled, filled.Sum(p => p.Total)));
    }

    /// <summary>
    /// Historical evaluation aggregates for Admin. Read-only cross-workspace SQL, no candidate
    /// fields and no query-filter bypass. Period is days (7/14/30/90) or all-time.
    /// Evaluation dates are the parent Run creation dates in Vietnam timezone.
    /// All-time chart groups by month. Stored AiScore does not prove a real provider invocation.
    /// </summary>
    private static async Task<IResult> AiHistoryAsync(
        string? period, SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var selected = string.IsNullOrEmpty(period) ? "14" : period;
        if (selected is not ("7" or "14" or "30" or "90" or "all"))
            return BadRequest("period chỉ nhận 7, 14, 30, 90 hoặc all.");
        var all = selected == "all";
        var (from, to, today) = VnRange(clock, all ? 90 :
            int.Parse(selected, System.Globalization.CultureInfo.InvariantCulture));
        void Parameters(DbCommand c)
        {
            Add(c, "@all", all ? 1 : 0);
            Add(c, "@from", from);
            Add(c, "@to", to);
        }

        // Do not JOIN Evaluations here: a run with 0 evaluations still counts as a run.
        const string runsSql = """
            SELECT r.ModelName, r.RunType, r.Status, COUNT_BIG(*),
                   MIN(r.CreatedAt), MAX(r.CreatedAt)
            FROM dbo.EvaluationRuns AS r
            WHERE @all = 1 OR (r.CreatedAt >= @from AND r.CreatedAt < @to)
            GROUP BY r.ModelName, r.RunType, r.Status
            """;
        var runs = await ReadAsync(db, runsSql, Parameters,
            r => new AdminRunHistoryGroup(r.GetString(0), r.GetString(1), r.GetString(2),
                Long(r, 3), r.IsDBNull(4) ? null : r.GetDateTime(4),
                r.IsDBNull(5) ? null : r.GetDateTime(5)), ct);

        // Query returns grouped counts only, never names, identifiers, scores, CV content or AI text.
        // Bucket according to Vietnam local date of run creation. For all-time, group by month.
        const string evaluationsSql = """
            SELECT r.ModelName, r.RunType,
              CASE WHEN @all = 1 THEN DATEFROMPARTS(YEAR(v.VnDate), MONTH(v.VnDate), 1)
                   ELSE v.VnDate END AS Bucket,
              e.Status, COALESCE(e.ScoreSource, N'Unknown') AS ScoreSource,
              COUNT_BIG(*) AS EvaluationCount,
              SUM(CASE WHEN e.AiScore IS NOT NULL THEN CONVERT(bigint, 1)
                  ELSE CONVERT(bigint, 0) END) AS HasAiScore
            FROM dbo.Evaluations AS e
            INNER JOIN dbo.EvaluationRuns AS r ON r.RunId = e.RunId
            CROSS APPLY (SELECT CONVERT(date, DATEADD(hour, 7, r.CreatedAt)) AS VnDate) AS v
            WHERE @all = 1 OR (r.CreatedAt >= @from AND r.CreatedAt < @to)
            GROUP BY r.ModelName, r.RunType,
              CASE WHEN @all = 1 THEN DATEFROMPARTS(YEAR(v.VnDate), MONTH(v.VnDate), 1)
                   ELSE v.VnDate END,
              e.Status, COALESCE(e.ScoreSource, N'Unknown')
            ORDER BY Bucket, r.ModelName, r.RunType
            """;
        var evaluations = await ReadAsync(db, evaluationsSql, Parameters,
            r => new AdminEvaluationHistoryGroup(DateOnly.FromDateTime(r.GetDateTime(2)),
                r.GetString(0), r.GetString(1), r.GetString(3), r.GetString(4),
                Long(r, 5), Long(r, 6)), ct);

        return Results.Ok(AdminAiHistoryRollup.Build(clock.GetUtcNow().UtcDateTime,
            selected, today, runs, evaluations));
    }

    private static async Task<IResult> WorkspacesAsync(int? page, string? search,
        SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = page ?? 1;
        if (p is < 1 or > 100000) return BadRequest("Trang phải từ 1 đến 100.000.");
        var q = (search ?? "").Trim();
        if (q.Length > 60 || q.Any(char.IsControl)) return BadRequest("Từ khóa tối đa 60 ký tự.");
        // Escape LIKE metacharacters: search is literal text, not user-supplied SQL pattern.
        var like = "%" + q.Replace("\\", "\\\\").Replace("%", "\\%")
            .Replace("_", "\\_").Replace("[", "\\[") + "%";
        var (from, to, _) = VnRange(clock, 1);
        const string countSql = "SELECT COUNT_BIG(*) FROM dbo.Workspaces WHERE (@q = N'' OR Name LIKE @like ESCAPE '\\')";
        var totals = await ReadAsync(db, countSql,
            c => { Add(c, "@q", q); Add(c, "@like", like); }, r => Long(r, 0), ct);
        var total = totals.Single();
        const int size = 20;
        // Page FIRST, then compute aggregates for at most 20 workspaces; do not
        // execute costly OUTER APPLY counts for every matching workspace.
        const string sql = """
            WITH PageRows AS (
              SELECT WorkspaceId, Name, Kind, IsActive, DailyAiEvaluationLimit, MaxCvCount
              FROM dbo.Workspaces
              WHERE (@q = N'' OR Name LIKE @like ESCAPE '\')
              ORDER BY WorkspaceId
              OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            )
            SELECT w.WorkspaceId, w.Name, w.Kind, w.IsActive, w.DailyAiEvaluationLimit, w.MaxCvCount,
              COALESCE(a.UsedUnits, 0), COALESCE(c.StoredCvs, 0), COALESCE(r.TotalRuns, 0)
            FROM PageRows AS w
            OUTER APPLY (SELECT SUM(CONVERT(bigint, e.Units - e.UnitsReleased)) AS UsedUnits
              FROM dbo.AiUsageEntries e WHERE e.WorkspaceId = w.WorkspaceId
                AND e.CreatedAtUtc >= @from AND e.CreatedAtUtc < @to) AS a
            OUTER APPLY (SELECT COUNT_BIG(*) AS StoredCvs FROM dbo.Cvs c
              WHERE c.WorkspaceId = w.WorkspaceId AND c.AnonymizedAt IS NULL) AS c
            OUTER APPLY (SELECT COUNT_BIG(*) AS TotalRuns FROM dbo.EvaluationRuns r
              WHERE r.WorkspaceId = w.WorkspaceId) AS r
            ORDER BY w.WorkspaceId
            """;
        var rows = await ReadAsync(db, sql, c =>
        {
            Add(c, "@q", q); Add(c, "@like", like);
            Add(c, "@from", from); Add(c, "@to", to);
            Add(c, "@skip", (p - 1) * size); Add(c, "@take", size);
        }, r => new AdminWorkspaceRowDto(
            r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetBoolean(3),
            OptionalInt(r, 4), OptionalInt(r, 5), Long(r, 6), Long(r, 7), Long(r, 8)), ct);
        return Results.Ok(new AdminWorkspacePageDto(p, size, total, rows));
    }

    private static async Task<IResult> ChangeQuotaAsync(int id, AdminQuotaChangeRequest change,
        SmartCvDbContext db, AuditTrail audit, CancellationToken ct)
    {
        if (id <= 0) return BadRequest("Mã workspace không hợp lệ.");
        var error = AdminQuotaRules.Validate(change.DailyAiEvaluationLimit, change.MaxCvCount, change.Reason);
        if (error is not null) return BadRequest(error);
        // The Internal workspace is deliberately not editable here; null means unlimited
        // for Internal only. Do not let an Admin UI turn Internal quotas into personal quotas.
        var ws = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(w => w.WorkspaceId == id, ct);
        if (ws is null) return Results.NotFound();
        if (ws.Kind != WorkspaceKinds.Personal)
            return Results.Problem(statusCode: 403, title: "Quota nội bộ không chỉnh sửa tại đây.");
        if (!ws.IsActive)
            return Results.Conflict(new { message = "Workspace không hoạt động; không được sửa quota." });

        if (ws.DailyAiEvaluationLimit != change.ExpectedDailyAiEvaluationLimit ||
            ws.MaxCvCount != change.ExpectedMaxCvCount)
            return Results.Conflict(new { message = "Quota đã thay đổi bởi người khác. Hãy tải lại trước khi lưu." });

        if (ws.DailyAiEvaluationLimit == change.DailyAiEvaluationLimit && ws.MaxCvCount == change.MaxCvCount)
            return Results.Ok(new { status = "unchanged", workspaceId = id });

        if (!db.Database.IsSqlServer())
            return Results.Problem(statusCode: 503, title: "Cập nhật quota yêu cầu SQL Server.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var query = db.Workspaces.Where(w => w.WorkspaceId == id && w.IsActive && w.Kind == WorkspaceKinds.Personal &&
            w.DailyAiEvaluationLimit == change.ExpectedDailyAiEvaluationLimit &&
            w.MaxCvCount == change.ExpectedMaxCvCount);
        var rows = await query.ExecuteUpdateAsync(s => s
            .SetProperty(w => w.DailyAiEvaluationLimit, change.DailyAiEvaluationLimit)
            .SetProperty(w => w.MaxCvCount, change.MaxCvCount), ct);
        if (rows != 1) return Results.Conflict(new { message = "Quota đã thay đổi đồng thời. Hãy tải lại." });

        var evt = audit.Stage(db, "workspace.quota_changed", id.ToString(), workspaceId: id);
        evt.SafeSummary = $"Đổi quota workspace {id}: AI {ws.DailyAiEvaluationLimit?.ToString() ?? "không giới hạn"} → {change.DailyAiEvaluationLimit}, CV {ws.MaxCvCount?.ToString() ?? "không giới hạn"} → {change.MaxCvCount}; lý do {change.Reason}";
        await db.SaveChangesAsync(ct); // Audit and quota live in the SAME transaction.
        await tx.CommitAsync(ct);
        audit.ConfirmCommitted();
        return Results.Ok(new { status = "updated", workspaceId = id,
            dailyAiEvaluationLimit = change.DailyAiEvaluationLimit, maxCvCount = change.MaxCvCount });
    }

    private static IResult IntegrationsAsync(IConfiguration cfg, TimeProvider clock)
    {
        var googleEnabled = cfg.GetValue<bool>("Authentication:Google:Enabled");
        var client = cfg["Authentication:Google:ClientId"];
        var clientSecret = cfg["Authentication:Google:ClientSecret"];
        var geminiKey = cfg["Gemini:ApiKey"];
        var model = cfg["Scoring:ModelName"] ?? "(chưa cấu hình)";
        var hint = string.IsNullOrEmpty(client) ? null :
            "••••" + client.Substring(Math.Max(0, client.Length - 6));
        return Results.Ok(new AdminIntegrationSnapshotDto(clock.GetUtcNow().UtcDateTime,
            model, !string.IsNullOrWhiteSpace(geminiKey), cfg.GetValue<bool>("Gemini:UseFake"),
            cfg.GetValue("Gemini:TimeoutSeconds", 90), cfg.GetValue("Gemini:MinDelayBetweenCallsMs", 4500),
            googleEnabled, !string.IsNullOrWhiteSpace(client) && !string.IsNullOrWhiteSpace(clientSecret),
            hint, "/signin-google", "Chỉ xác nhận cấu hình; chưa kiểm tra kết nối nhà cung cấp."));
    }

    /// <summary>
    /// Only existing persisted operational records. No data-provider request and no guessed tokens/costs.
    /// Current auth provider implementation logs only Google external authentication; future providers
    /// require a provider-specific audit dimension before these counts can remain Google-specific.
    /// </summary>
    private static async Task<IResult> ProviderInsightsAsync(
        SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var captured = clock.GetUtcNow().UtcDateTime;
        var from = captured.AddDays(-7);
        const string sql = """
            SELECT
              (SELECT COUNT_BIG(*) FROM dbo.UserExternalLogins WHERE Provider = N'google'),
              (SELECT COUNT_BIG(*) FROM dbo.AuditEvents WHERE ActionCode = 'auth.external_login'
                 AND Outcome = 'Succeeded' AND OccurredAtUtc >= @since),
              (SELECT COUNT_BIG(*) FROM dbo.AuditEvents WHERE ActionCode = 'auth.external_register'
                 AND Outcome = 'Succeeded' AND OccurredAtUtc >= @since),
              (SELECT COUNT_BIG(*) FROM dbo.AuditEvents WHERE ActionCode = 'auth.external_rejected'
                 AND OccurredAtUtc >= @since),
              (SELECT MAX(OccurredAtUtc) FROM dbo.AuditEvents WHERE ActionCode = 'auth.external_login'
                 AND Outcome = 'Succeeded'),
              (SELECT COUNT_BIG(*) FROM dbo.Evaluations AS e
                 INNER JOIN dbo.EvaluationRuns AS r ON r.RunId = e.RunId
                 WHERE e.AiScore IS NOT NULL AND e.Status = 'Completed'
                 AND r.CreatedAt >= @since),
              (SELECT COALESCE(SUM(CONVERT(bigint, Units - UnitsReleased)), 0)
                 FROM dbo.AiUsageEntries WHERE CreatedAtUtc >= @since)
            """;
        var rows = await ReadAsync(db, sql, c => Add(c, "@since", from),
            r => new AdminProviderInsightsDto(captured, Long(r, 0), Long(r, 1),
                Long(r, 2), Long(r, 3), r.IsDBNull(4) ? null : r.GetDateTime(4),
                Long(r, 5), Long(r, 6)), ct);
        return Results.Ok(rows.Single());
    }

    /// <summary>
    /// Read-only live check. models.get does NOT generate content and does NOT prove the
    /// scoring request succeeds. Bounded to 10 seconds and explicit user interaction.
    /// No Google response body, key or client secret is ever returned to the UI.
    /// </summary>
    private static async Task<IResult> CheckGeminiAsync(
        IConfiguration cfg, IHttpClientFactory clients, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var model = cfg["Scoring:ModelName"] ?? "";
        var key = cfg["Gemini:ApiKey"];
        IResult Reply(string state, int? code, bool supported, string detail) =>
            Results.Ok(new AdminGeminiCheckDto(now, model, state, code, supported, detail));

        if (cfg.GetValue<bool>("Gemini:UseFake"))
            return Reply("simulated", null, false, "AI giả lập đang bật. Không kiểm tra Google.");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model))
            return Reply("not_configured", null, false, "Thiếu API key hoặc model.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var client = clients.CreateClient("Gemini");
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"models/{Uri.EscapeDataString(model)}");
            request.Headers.Add("x-goog-api-key", key);
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var detail = status switch
                {
                    401 or 403 => "Google từ chối xác thực hoặc quyền API key.",
                    404 => "Model không khả dụng với API key hoặc tên model sai.",
                    429 => "Google giới hạn tần suất. Kiểm tra quota nhà cung cấp.",
                    >= 500 => "Dịch vụ Google tạm thời không khả dụng.",
                    _ => "Google trả về mã HTTP " + status + "."
                };
                return Reply("failed", status, false, detail);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var doc = await JsonDocument.ParseAsync(stream,
                new JsonDocumentOptions { MaxDepth = 16 }, timeout.Token);
            var hasMethods = doc.RootElement.TryGetProperty("supportedGenerationMethods", out var methods)
                && methods.ValueKind == JsonValueKind.Array;
            var canGenerate = hasMethods && methods.EnumerateArray().Any(x =>
                x.ValueKind == JsonValueKind.String && x.GetString() == "generateContent");
            return Reply("reachable", status, canGenerate,
                canGenerate
                    ? "Google xác nhận models.get và liệt kê generateContent. Cần chấm CV thật để xác minh đầu-cuối."
                    : "models.get trả HTTP 200, nhưng chưa xác minh generateContent. Hãy kiểm tra model.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Reply("timeout", null, false, "Kiểm tra Google quá 10 giây.");
        }
        catch (HttpRequestException)
        {
            return Reply("network_error", null, false, "Không kết nối được Google API.");
        }
        catch (JsonException)
        {
            return Reply("invalid_response", null, false, "Google trả thông tin model không đọc được.");
        }
    }

    private static IResult BadRequest(string detail) => Results.Problem(
        title: "Dữ liệu không hợp lệ", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private static long Long(DbDataReader r, int index) => r.IsDBNull(index) ? 0 : Convert.ToInt64(r.GetValue(index));
    private static int? OptionalInt(DbDataReader r, int index) => r.IsDBNull(index) ? null : r.GetInt32(index);
    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p);
    }

    /// <summary>Single connection per query. SQL text is constant; all external data is passed as parameters.</summary>
    private static async Task<List<T>> ReadAsync<T>(SmartCvDbContext db, string sql,
        Action<DbCommand> parameters, Func<DbDataReader, T> map, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
            throw new NotSupportedException("Báo cáo Admin Operations cần SQL Server và migration A54/B43 đã áp dụng.");
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql; command.CommandType = CommandType.Text;
            command.CommandTimeout = 15;
            parameters(command);
            var items = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(map(reader));
            return items;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
