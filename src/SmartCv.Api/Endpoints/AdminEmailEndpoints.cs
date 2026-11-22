using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Email;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

public static class AdminEmailEndpoints
{
    public static IEndpointRouteBuilder MapAdminEmailEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/email").WithTags("Admin Email")
            .RequireAuthorization(AuthPolicies.Admin);
        group.MapPost("/test", EnqueueTestAsync).WithName("AdminEmailTest")
            .Produces(202).Produces(429);
        group.MapGet("/health", HealthAsync).WithName("AdminEmailHealth");
        return app;
    }

    private static async Task<IResult> EnqueueTestAsync(HttpContext http, SmartCvDbContext db,
        IEmailQueue queue, TimeProvider clock, CancellationToken ct)
    {
        if (!int.TryParse(http.User.FindFirst(SmartCvClaims.UserId)?.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return Results.Unauthorized();
        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = db.Database.IsSqlServer() ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
        // Serialization per Admin qua chính User row, không dùng static rate counter có thể bị bypass giữa hai API instances.
        var admin = db.Database.IsSqlServer()
            ? await db.Users.FromSqlInterpolated($"SELECT * FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE UserId={id}")
                .AsNoTracking().SingleOrDefaultAsync(ct)
            : await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == id, ct);
        if (admin is not { IsActive: true } || admin.Role != "Admin")
            return Results.Forbid();
        var n = await db.EmailOutbox.CountAsync(x => x.UserId == id && x.Template == EmailTemplate.AdminDiagnostic.ToString()
            && x.CreatedAt >= now.AddHours(-1), ct);
        if (n >= 3) return Results.Problem(statusCode: 429, title: "Đã đạt giới hạn 3 email thử/giờ.",
            extensions: new Dictionary<string, object?> { ["code"] = "email_test_rate_limited" });
        await queue.EnqueueAsync(id, EmailTemplate.AdminDiagnostic,
            new EmailTemplateModel(admin.Email, admin.FullName), ct);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return Results.Accepted(value: new { status = "queued" });
    }

    private static async Task<IResult> HealthAsync(SmartCvDbContext db, IOptions<EmailOptions> options,
        TimeProvider clock, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime.AddHours(-24);
        var pending24h = await db.EmailOutbox.CountAsync(x => x.Status == "Pending" && x.CreatedAt >= cutoff, ct);
        var failed24h = await db.EmailOutbox.CountAsync(x => x.Status == "Failed" && x.CreatedAt >= cutoff, ct);
        return Results.Ok(new { mode = options.Value.Mode, pending24h, failed24h, workerEnabled = options.Value.WorkerEnabled });
    }
}
