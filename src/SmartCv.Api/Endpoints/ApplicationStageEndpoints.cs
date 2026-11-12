using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartCv.Core.Applications;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// A35: HR đổi giai đoạn hồ sơ, mọi lần đổi ghi một dòng ApplicationEvents.
///   GET   /api/applications/stage-options      6 giai đoạn + 6 lý do loại (chữ hiển thị)
///   PATCH /api/applications/{id}/stage         đổi một hồ sơ
///   POST  /api/applications/stage              đổi nhiều hồ sơ một lần (một giao dịch)
///   GET   /api/applications/{id}/events        lịch sử, mới nhất trước
/// AI KHÔNG gọi các API này: điểm chỉ là gợi ý, quyết định là của HR.
/// </summary>
public static class ApplicationStageEndpoints
{
    public static WebApplication MapApplicationStageEndpoints(this WebApplication app)
    {
        // Keep the existing A8/A13 personal-data access boundary until authentication is introduced.
        if (!app.Environment.IsDevelopment() ||
            !app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled) return app;
        var group = app.MapGroup("/api/applications").WithTags("Applications");
        group.MapGet("/stage-options", Options).WithName("StageOptions").WithSummary("Các giai đoạn và lý do loại");
        group.MapPatch("/{id:int}/stage", ChangeOneAsync).WithName("ChangeStage").WithSummary("Đổi giai đoạn một hồ sơ");
        group.MapPost("/stage", ChangeManyAsync).WithName("ChangeStageBulk").WithSummary("Đổi giai đoạn nhiều hồ sơ");
        group.MapGet("/{id:int}/events", EventsAsync).WithName("ApplicationEvents").WithSummary("Lịch sử quyết định của một hồ sơ");
        return app;
    }

    // ---------- GET /api/applications/stage-options ----------
    private static Ok<StageOptionsResponse> Options() => TypedResults.Ok(new StageOptionsResponse
    {
        Stages = ApplicationStages.All.Select(s => new StageOptionDto { Code = s, DisplayName = ApplicationStages.DisplayName(s) }).ToList(),
        RejectReasons = RejectReasons.All.Select(r => new StageOptionDto { Code = r, DisplayName = RejectReasons.DisplayName(r) }).ToList()
    });

    // ---------- PATCH /api/applications/{id}/stage ----------
    private static async Task<Results<Ok<StageChangeResultDto>, ProblemHttpResult>> ChangeOneAsync(
        int id, ChangeStageRequest request, SmartCvDbContext db, ILogger<ApplicationEvent> logger, CancellationToken ct)
    {
        var (change, error) = StageChangeRules.Validate(request.ToStage, request.RejectReason, request.Note, request.Actor);
        if (change is null) return Problem(400, "Dữ liệu không hợp lệ", error!);
        if (id <= 0 || (request.ExpectedStage is not null && !ApplicationStages.IsValid(request.ExpectedStage.Trim()))
            || request.ExpectedStageVersion == Guid.Empty)
            return Problem(400, "Dữ liệu không hợp lệ", "Mã hồ sơ, giai đoạn hoặc phiên bản dự kiến không hợp lệ.");

        var app = await db.Applications.FirstOrDefaultAsync(a => a.ApplicationId == id, ct);
        if (app is null) return Problem(404, "Không tìm thấy", $"Không có hồ sơ #{id}.");

        // Hai người cùng mở trang: người sau thấy giai đoạn cũ → báo để tải lại, không ghi đè quyết định của người trước
        if ((request.ExpectedStage is not null && request.ExpectedStage.Trim() != app.Stage)
            || (request.ExpectedStageVersion is { } version && version != app.StageVersion))
            return Problem(409, "Hồ sơ vừa được đổi",
                $"Hồ sơ đang ở \"{ApplicationStages.DisplayName(app.Stage)}\" (có người vừa đổi). Hãy tải lại trang rồi thao tác lại.");

        if (StageChangeRules.IsNoOp(new CurrentStage(app.ApplicationId, app.Stage, app.RejectReason), change))
            return TypedResults.Ok(Unchanged(app, "Hồ sơ đã ở giai đoạn này."));

        var ev = Apply(app, change, DateTime.UtcNow);
        db.ApplicationEvents.Add(ev);
        try
        {
            await db.SaveChangesAsync(ct); // EF checks original concurrency tokens in the UPDATE predicate.
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(); // SaveChanges rolls back the event as well; never retry an HR decision silently.
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 1205 or 547 })
        {
            // Concurrent deletion (FK) or overlapping SQL Server transactions (deadlock victim).
            return Conflict();
        }

        logger.LogInformation("Hồ sơ {Id}: {From} → {To} ({Reason}) bởi {Actor}", id, ev.FromStage, ev.ToStage, ev.RejectReason, ev.Actor);
        return TypedResults.Ok(Changed(app, ev));
    }

    // ---------- POST /api/applications/stage ----------
    private static async Task<Results<Ok<BulkStageChangeResponse>, ProblemHttpResult>> ChangeManyAsync(
        BulkChangeStageRequest request, SmartCvDbContext db, ILogger<ApplicationEvent> logger, CancellationToken ct)
    {
        var idError = StageChangeRules.ValidateIds(request.ApplicationIds);
        if (idError is not null) return Problem(400, "Danh sách hồ sơ không hợp lệ", idError);
        if (request.ExpectedStageVersions is { } versions &&
            versions.Any(v => !request.ApplicationIds.Contains(v.Key) || v.Value == Guid.Empty))
            return Problem(400, "Dữ liệu không hợp lệ", "Phiên bản dự kiến phải thuộc hồ sơ đã chọn và không được rỗng.");

        var (change, error) = StageChangeRules.Validate(request.ToStage, request.RejectReason, request.Note, request.Actor);
        if (change is null) return Problem(400, "Dữ liệu không hợp lệ", error!);

        var ids = request.ApplicationIds;
        var apps = await db.Applications.Where(a => ids.Contains(a.ApplicationId)).ToDictionaryAsync(a => a.ApplicationId, ct);
        var current = apps.Values.ToDictionary(a => a.ApplicationId, a => new CurrentStage(a.ApplicationId, a.Stage, a.RejectReason));
        var plan = StageChangeRules.Plan(ids, current, change);
        var stale = apps.Values.Where(a => request.ExpectedStageVersions is not null
            && request.ExpectedStageVersions.TryGetValue(a.ApplicationId, out var version)
            && version != a.StageVersion).Select(a => a.ApplicationId).ToHashSet();
        if (stale.Count > 0) return BulkConflict(plan, apps, stale);

        var now = DateTime.UtcNow;
        var events = new Dictionary<int, ApplicationEvent>();
        foreach (var p in plan.Where(p => p.Outcome == StageChangeOutcome.Changed).OrderBy(p => p.ApplicationId))
        {
            var ev = Apply(apps[p.ApplicationId], change, now);
            db.ApplicationEvents.Add(ev);
            events[p.ApplicationId] = ev;
        }

        // MỘT lần SaveChanges = MỘT giao dịch: lỗi giữa chừng thì không hồ sơ nào bị đổi
        try
        {
            if (events.Count > 0) await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var conflicts = ex.Entries.Where(e => e.Entity is Application)
                .Select(e => ((Application)e.Entity).ApplicationId).ToHashSet();
            db.ChangeTracker.Clear();
            var saved = await db.Applications.AsNoTracking().Where(a => ids.Contains(a.ApplicationId))
                .ToDictionaryAsync(a => a.ApplicationId, ct);
            return BulkConflict(plan, saved, conflicts);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 1205 or 547 })
        {
            db.ChangeTracker.Clear();
            var saved = await db.Applications.AsNoTracking().Where(a => ids.Contains(a.ApplicationId))
                .ToDictionaryAsync(a => a.ApplicationId, ct);
            return BulkConflict(plan, saved, events.Keys.ToHashSet());
        }
        logger.LogInformation("Đổi hàng loạt sang {To}: {Changed} đổi, {Same} giữ nguyên, {Missing} không có, bởi {Actor}",
            change.ToStage, events.Count, plan.Count(p => p.Outcome == StageChangeOutcome.Unchanged),
            plan.Count(p => p.Outcome == StageChangeOutcome.NotFound), change.Actor);

        var results = plan.Select(p => p.Outcome switch
        {
            StageChangeOutcome.Changed => Changed(apps[p.ApplicationId], events[p.ApplicationId]),
            StageChangeOutcome.Unchanged => Unchanged(apps[p.ApplicationId], "Hồ sơ đã ở giai đoạn này."),
            _ => new StageChangeResultDto
            {
                ApplicationId = p.ApplicationId,
                Outcome = nameof(StageChangeOutcome.NotFound),
                Message = $"Không có hồ sơ #{p.ApplicationId}."
            }
        }).ToList();

        return TypedResults.Ok(new BulkStageChangeResponse
        {
            Total = results.Count,
            Changed = results.Count(r => r.Outcome == nameof(StageChangeOutcome.Changed)),
            Unchanged = results.Count(r => r.Outcome == nameof(StageChangeOutcome.Unchanged)),
            NotFound = results.Count(r => r.Outcome == nameof(StageChangeOutcome.NotFound)),
            Results = results
        });
    }

    // ---------- GET /api/applications/{id}/events ----------
    private static async Task<Results<Ok<List<ApplicationEventDto>>, ProblemHttpResult>> EventsAsync(
        int id, SmartCvDbContext db, CancellationToken ct)
    {
        if (!await db.Applications.AnyAsync(a => a.ApplicationId == id, ct))
            return Problem(404, "Không tìm thấy", $"Không có hồ sơ #{id}.");

        var rows = await db.ApplicationEvents.AsNoTracking()
            .Where(e => e.ApplicationId == id)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.EventId)
            .ToListAsync(ct);

        return TypedResults.Ok(rows.Select(e => new ApplicationEventDto
        {
            EventId = e.EventId,
            ApplicationId = e.ApplicationId,
            FromStage = e.FromStage,
            ToStage = e.ToStage,
            RejectReason = e.RejectReason,
            Note = e.Note,
            Actor = e.Actor,
            CreatedAt = Utc(e.CreatedAt)
        }).ToList());
    }

    // ---------- dùng chung ----------

    /// <summary>Đổi giai đoạn trên entity và tạo dòng lịch sử tương ứng (chưa lưu).</summary>
    private static ApplicationEvent Apply(Application app, StageChange change, DateTime now)
    {
        var ev = new ApplicationEvent
        {
            ApplicationId = app.ApplicationId,
            FromStage = app.Stage,
            ToStage = change.ToStage,
            RejectReason = change.RejectReason,
            Note = change.Note,
            Actor = change.Actor,
            CreatedAt = now
        };
        app.Stage = change.ToStage;
        app.RejectReason = change.RejectReason;
        app.StageUpdatedAt = now;
        return ev;
    }

    private static StageChangeResultDto Changed(Application app, ApplicationEvent ev) => new()
    {
        ApplicationId = app.ApplicationId,
        Outcome = nameof(StageChangeOutcome.Changed),
        FromStage = ev.FromStage,
        Stage = app.Stage,
        RejectReason = app.RejectReason,
        StageUpdatedAt = app.StageUpdatedAt is { } d ? Utc(d) : null,
        StageVersion = app.StageVersion,
        EventId = ev.EventId
    };

    private static StageChangeResultDto Unchanged(Application app, string message) => new()
    {
        ApplicationId = app.ApplicationId,
        Outcome = nameof(StageChangeOutcome.Unchanged),
        FromStage = app.Stage,
        Stage = app.Stage,
        RejectReason = app.RejectReason,
        StageUpdatedAt = app.StageUpdatedAt is { } d ? Utc(d) : null,
        StageVersion = app.StageVersion,
        Message = message
    };

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private const string ConflictMessage = "Hồ sơ vừa được người khác thay đổi. Hãy tải lại dữ liệu rồi thao tác lại.";

    private static ProblemHttpResult Conflict() => Problem(409, "Hồ sơ vừa được đổi", ConflictMessage);

    private static ProblemHttpResult BulkConflict(List<StageChangePlan> plan,
        IReadOnlyDictionary<int, Application> saved, HashSet<int> conflicts)
    {
        // No Changed outcome or event ID is returned after a rolled-back transaction.
        var results = plan.Select(p =>
        {
            saved.TryGetValue(p.ApplicationId, out var app);
            return new StageChangeResultDto
            {
                ApplicationId = p.ApplicationId,
                Outcome = app is null ? "NotFound" : conflicts.Contains(p.ApplicationId) ? "Conflict" : "RolledBack",
                FromStage = p.FromStage,
                Stage = app?.Stage,
                RejectReason = app?.RejectReason,
                StageUpdatedAt = app?.StageUpdatedAt is { } d ? Utc(d) : null,
                StageVersion = app?.StageVersion,
                Message = app is null ? $"Không có hồ sơ #{p.ApplicationId}." : "Không lưu thao tác hàng loạt. " + ConflictMessage
            };
        }).ToList();
        return TypedResults.Problem(statusCode: 409, title: "Hồ sơ vừa được đổi",
            detail: "Toàn bộ thao tác hàng loạt đã hủy. " + ConflictMessage,
            extensions: new Dictionary<string, object?> { ["results"] = results, ["rolledBack"] = true });
    }

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: status);
}
