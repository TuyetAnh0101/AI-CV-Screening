using Microsoft.EntityFrameworkCore;
using SmartCv.Api.Auth;
using SmartCv.Core.Auditing;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/audit-events").WithTags("Audit").RequireAuthorization(AuthPolicies.Admin);
        group.MapGet("/", ListAsync).WithName("ListAuditEvents").Produces<AuditPageDto>();
        group.MapGet("/{id:long}", GetAsync).WithName("GetAuditEvent").Produces<AuditEventDto>();
        return app;
    }

    private static async Task<IResult> ListAsync(HttpContext http, SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (filter, errors) = AuditQuery.Parse(http.Request.Query.ToDictionary(p => p.Key, p => p.Value.Select(v => v ?? "").ToArray(), StringComparer.OrdinalIgnoreCase), clock.GetUtcNow().UtcDateTime);
        if (filter is null) return Results.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["code"] = "audit_invalid_filter" });
        try
        {
            var query = db.AuditEvents.AsNoTracking().Where(e => e.OccurredAtUtc >= filter.FromUtc && e.OccurredAtUtc < filter.UntilUtc);
            if (filter.Category is { } category) query = query.Where(e => e.Category == category);
            if (filter.Action is { } action) query = query.Where(e => e.ActionCode == action);
            if (filter.ActorUserId is { } actor) query = query.Where(e => e.ActorUserId == actor);
            if (filter.Outcome is { } outcome) query = query.Where(e => e.Outcome == outcome);
            if (filter.ResourceType is { } type) query = query.Where(e => e.ResourceType == type);
            if (filter.ResourceId is { } resource) query = query.Where(e => e.ResourceId == resource);
            if (filter.RequestId is { } request) query = query.Where(e => e.RequestId == request);
            if (filter.Search is { } search) query = query.Where(e => e.ActionCode.Contains(search) || e.SafeSummary.Contains(search)
                || (e.ResourceId != null && e.ResourceId.Contains(search)) || e.RequestId.Contains(search));
            var total = await query.CountAsync(ct);
            var logins = await query.CountAsync(e => e.ActionCode == "auth.login", ct);
            var accounts = await query.CountAsync(e => e.Category == "Accounts", ct);
            var failures = await query.CountAsync(e => e.Outcome == "Failed" || e.Outcome == "Denied" || e.Outcome == "Partial", ct);
            var rows = await query.OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.AuditEventId)
                .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToListAsync(ct);
            return Results.Ok(new AuditPageDto { Page = filter.Page, PageSize = filter.PageSize, TotalCount = total,
                LoginCount = logins, AccountActionCount = accounts, FailureCount = failures, Items = rows.Select(ToDto).ToList() });
        }
        catch (Exception error) when (AuthProblems.IsDatabaseFailure(error)) { return Unavailable(); }
    }

    private static async Task<IResult> GetAsync(long id, SmartCvDbContext db, CancellationToken ct)
    {
        if (id <= 0) return Results.Problem(statusCode: 400, title: "Mã sự kiện phải là số nguyên dương.", extensions: new Dictionary<string, object?> { ["code"] = "audit_invalid_id" });
        try
        {
            var row = await db.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.AuditEventId == id, ct);
            return row is null ? Results.Problem(statusCode: 404, title: "Không tìm thấy sự kiện.", extensions: new Dictionary<string, object?> { ["code"] = "audit_not_found" }) : Results.Ok(ToDto(row));
        }
        catch (Exception error) when (AuthProblems.IsDatabaseFailure(error)) { return Unavailable(); }
    }
    private static IResult Unavailable() => Results.Problem(statusCode: 503, title: "Nhật ký hoạt động tạm không khả dụng.",
        detail: "Kiểm tra kết nối và schema AuditEvents đã được phê duyệt trước khi thử lại.", extensions: new Dictionary<string, object?> { ["code"] = "audit_unavailable" });
    private static AuditEventDto ToDto(AuditEvent e) => new(e.AuditEventId, DateTime.SpecifyKind(e.OccurredAtUtc, DateTimeKind.Utc),
        e.ActorUserId, e.ActorNameSnapshot, e.ActorRoleSnapshot, e.ActionCode, e.Category, e.ResourceType, e.ResourceId,
        e.Outcome, e.RequestId, e.SafeSummary, e.FailureCode);
}
