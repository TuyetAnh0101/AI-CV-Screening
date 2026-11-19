using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Evaluation;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// SCApp v2: cờ "Cần kiểm tra lại" do HR gắn thủ công.
///   GET  /api/applications/flag-reasons                 các lý do (chữ hiển thị)
///   GET  /api/applications/{id}/flags                   cờ đang mở, mọi cờ và lịch sử
///   POST /api/applications/{id}/flags                   đánh dấu cần kiểm tra (409 nếu đã có cờ đang mở)
///   POST /api/applications/flags/{flagId}/resolve       đánh dấu đã xử lý (ExpectedVersion bắt buộc, 409 khi lệch)
///   POST /api/applications/flags/{flagId}/reopen        mở lại (ExpectedVersion bắt buộc, 409 khi lệch)
/// KHÔNG đổi giai đoạn tuyển dụng, KHÔNG ghi ApplicationEvents, KHÔNG gọi AI.
/// Cùng ranh giới tạm thời với A35 (Development + CvUpload:Enabled) cho tới khi có xác thực thật.
/// </summary>
public static class ManualFlagEndpoints
{
    public static WebApplication MapManualFlagEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() ||
            !app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled) return app;
        var group = app.MapGroup("/api/applications").WithTags("ManualFlags");
        group.MapGet("/flag-reasons", Reasons).WithName("ManualFlagReasons").WithSummary("Lý do đánh dấu cần kiểm tra");
        group.MapGet("/{id:int}/flags", GetAsync).WithName("ApplicationFlags").WithSummary("Cờ thủ công và lịch sử của một hồ sơ");
        group.MapPost("/{id:int}/flags", CreateAsync).WithName("CreateApplicationFlag").WithSummary("Đánh dấu hồ sơ cần kiểm tra");
        group.MapPost("/flags/{flagId:int}/resolve", ResolveAsync).WithName("ResolveApplicationFlag").WithSummary("Đánh dấu cờ đã xử lý");
        group.MapPost("/flags/{flagId:int}/reopen", ReopenAsync).WithName("ReopenApplicationFlag").WithSummary("Mở lại cờ đã xử lý");
        return app;
    }

    private static Ok<List<ManualFlagReasonDto>> Reasons() => TypedResults.Ok(ManualFlagReasons.All
        .Select(r => new ManualFlagReasonDto { Code = r, DisplayName = ManualFlagReasons.DisplayName(r), NoteRequired = r == ManualFlagReasons.Other })
        .ToList());

    // ---------- GET /api/applications/{id}/flags ----------
    private static async Task<Results<Ok<ApplicationFlagsResponse>, ProblemHttpResult>> GetAsync(int id, SmartCvDbContext db, CancellationToken ct)
    {
        var anonymized = await db.Applications.AsNoTracking().Where(a => a.ApplicationId == id)
            .Select(a => (bool?)(a.Cv.AnonymizedAt != null)).FirstOrDefaultAsync(ct);
        if (anonymized is null) return Problem(404, "Không tìm thấy", $"Không có hồ sơ #{id}.");
        var flags = await db.ApplicationFlags.AsNoTracking().Where(f => f.ApplicationId == id)
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.FlagId).ToListAsync(ct);
        var flagIds = flags.Select(f => f.FlagId).ToList();
        var events = await db.ApplicationFlagEvents.AsNoTracking().Where(e => flagIds.Contains(e.FlagId))
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.EventId).ToListAsync(ct);
        var hide = anonymized.Value;
        var dtos = flags.Select(f => ToDto(f, hide)).ToList();
        return TypedResults.Ok(new ApplicationFlagsResponse
        {
            ApplicationId = id,
            Current = dtos.FirstOrDefault(f => f.Status == ManualFlagStatuses.Open),
            Flags = dtos,
            Events = events.Select(e => new ManualFlagEventDto
            {
                EventId = e.EventId, FlagId = e.FlagId, Action = e.Action, ActionName = ManualFlagActions.DisplayName(e.Action),
                Actor = e.Actor, Note = hide ? null : e.Note, CreatedAt = Utc(e.CreatedAt)
            }).ToList()
        });
    }

    // ---------- POST /api/applications/{id}/flags ----------
    private static async Task<Results<Created<ManualFlagDto>, ProblemHttpResult>> CreateAsync(
        int id, CreateManualFlagRequest request, SmartCvDbContext db, CancellationToken ct)
    {
        var error = ManualFlagRules.ValidateCreate(request.Reason, request.Note, request.Actor);
        if (error is not null) return Problem(400, "Dữ liệu không hợp lệ", error);
        var app = await db.Applications.AsNoTracking().Where(a => a.ApplicationId == id)
            .Select(a => new { a.ApplicationId, Anonymized = a.Cv.AnonymizedAt != null }).FirstOrDefaultAsync(ct);
        if (app is null) return Problem(404, "Không tìm thấy", $"Không có hồ sơ #{id}.");
        if (app.Anonymized) return Problem(400, "Hồ sơ đã ẩn danh", "Hồ sơ đã ẩn danh, không gắn cờ kiểm tra được nữa.");
        if (await db.ApplicationFlags.AnyAsync(f => f.ApplicationId == id && f.Status == ManualFlagStatuses.Open, ct))
            return OpenConflict(id);

        var now = DateTime.UtcNow;
        var actor = request.Actor.Trim();
        var note = ManualFlagRules.Clean(request.Note);
        var flag = new ApplicationFlag
        {
            ApplicationId = id, Reason = request.Reason.Trim(), Note = note, Status = ManualFlagStatuses.Open,
            CreatedBy = actor, CreatedAt = now, UpdatedAt = now, Version = Guid.NewGuid()
        };
        flag.Events.Add(new ApplicationFlagEvent { Action = ManualFlagActions.Opened, Actor = actor, Note = note, CreatedAt = now });
        db.ApplicationFlags.Add(flag);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.GetBaseException() is SqlException { Number: 2601 or 2627 })
        {
            return OpenConflict(id);   // hai HR gắn cờ cùng lúc: chỉ index duy nhất biết người nào đến trước
        }
        return TypedResults.Created($"/api/applications/{id}/flags", ToDto(flag, false));
    }

    // ---------- POST /api/applications/flags/{flagId}/resolve | reopen ----------
    private static Task<Results<Ok<ManualFlagDto>, ProblemHttpResult>> ResolveAsync(
        int flagId, ChangeManualFlagRequest request, SmartCvDbContext db, CancellationToken ct) => ChangeAsync(flagId, request, resolve: true, db, ct);

    private static Task<Results<Ok<ManualFlagDto>, ProblemHttpResult>> ReopenAsync(
        int flagId, ChangeManualFlagRequest request, SmartCvDbContext db, CancellationToken ct) => ChangeAsync(flagId, request, resolve: false, db, ct);

    private static async Task<Results<Ok<ManualFlagDto>, ProblemHttpResult>> ChangeAsync(
        int flagId, ChangeManualFlagRequest request, bool resolve, SmartCvDbContext db, CancellationToken ct)
    {
        var error = ManualFlagRules.ValidateChange(request.Note, request.Actor);
        if (error is not null) return Problem(400, "Dữ liệu không hợp lệ", error);
        if (request.ExpectedVersion is null || request.ExpectedVersion == Guid.Empty)
            return Problem(400, "Thiếu phiên bản", "Gửi ExpectedVersion của cờ đang hiển thị để tránh ghi đè thao tác của HR khác.");
        var flag = await db.ApplicationFlags.FirstOrDefaultAsync(f => f.FlagId == flagId, ct);
        if (flag is null) return Problem(404, "Không tìm thấy", $"Không có cờ #{flagId}.");
        if (flag.Version != request.ExpectedVersion) return VersionConflict(flag);

        var target = resolve ? ManualFlagStatuses.Resolved : ManualFlagStatuses.Open;
        if (flag.Status == target)
            return Problem(409, resolve ? "Cờ đã được xử lý" : "Cờ đang mở", "Trạng thái cờ không đổi. Hãy tải lại để xem thông tin mới nhất.");
        if (!resolve && await db.ApplicationFlags.AnyAsync(f => f.ApplicationId == flag.ApplicationId && f.Status == ManualFlagStatuses.Open && f.FlagId != flagId, ct))
            return OpenConflict(flag.ApplicationId);

        var now = DateTime.UtcNow;
        var actor = request.Actor.Trim();
        var note = ManualFlagRules.Clean(request.Note);
        flag.Status = target;
        flag.UpdatedAt = now;
        flag.Version = Guid.NewGuid();
        if (resolve) { flag.ResolvedBy = actor; flag.ResolvedAt = now; flag.ResolutionNote = note; }
        else { flag.ResolvedBy = null; flag.ResolvedAt = null; flag.ResolutionNote = null; }
        db.ApplicationFlagEvents.Add(new ApplicationFlagEvent
        {
            FlagId = flag.FlagId, Action = resolve ? ManualFlagActions.Resolved : ManualFlagActions.Reopened, Actor = actor, Note = note, CreatedAt = now
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return Problem(409, "Cờ vừa được thay đổi", "HR khác vừa cập nhật cờ này. Hãy tải lại rồi thử lại.");
        }
        catch (DbUpdateException ex) when (ex.GetBaseException() is SqlException { Number: 2601 or 2627 })
        {
            return OpenConflict(flag.ApplicationId);
        }
        return TypedResults.Ok(ToDto(flag, false));
    }

    internal static ManualFlagDto ToDto(ApplicationFlag f, bool hideNotes) => new()
    {
        FlagId = f.FlagId, ApplicationId = f.ApplicationId, Reason = f.Reason, ReasonName = ManualFlagReasons.DisplayName(f.Reason),
        // Ghi chú HR có thể nhắc thông tin cá nhân: ẩn với hồ sơ đã ẩn danh.
        Note = hideNotes ? null : f.Note, Status = f.Status, CreatedBy = f.CreatedBy, CreatedAt = Utc(f.CreatedAt),
        ResolvedBy = f.ResolvedBy, ResolvedAt = f.ResolvedAt is { } r ? Utc(r) : null,
        ResolutionNote = hideNotes ? null : f.ResolutionNote, UpdatedAt = Utc(f.UpdatedAt), Version = f.Version
    };

    private static ProblemHttpResult OpenConflict(int applicationId) => TypedResults.Problem(statusCode: 409, title: "Đã có cờ đang mở",
        detail: $"Hồ sơ #{applicationId} đã được đánh dấu cần kiểm tra. Hãy xử lý cờ hiện tại trước.",
        extensions: new Dictionary<string, object?> { ["code"] = "ManualFlagAlreadyOpen" });

    private static ProblemHttpResult VersionConflict(ApplicationFlag flag) => TypedResults.Problem(statusCode: 409, title: "Cờ vừa được thay đổi",
        detail: "HR khác vừa cập nhật cờ này. Hãy tải lại để xem trạng thái mới nhất trước khi thao tác.",
        extensions: new Dictionary<string, object?> { ["code"] = "ManualFlagVersionConflict", ["status"] = flag.Status });

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    private static ProblemHttpResult Problem(int status, string title, string detail) => TypedResults.Problem(statusCode: status, title: title, detail: detail);
}
