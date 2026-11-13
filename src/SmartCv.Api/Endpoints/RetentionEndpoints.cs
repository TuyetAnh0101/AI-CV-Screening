using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Privacy;

namespace SmartCv.Api.Endpoints;

public static class RetentionEndpoints
{
    public static WebApplication MapRetentionEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled)
            return app; // Same fail-closed boundary as CV endpoints until authenticated HR authorization is available.
        var group = app.MapGroup("/api/retention").WithTags("Retention");
        group.AddEndpointFilter(AuthorizedOperator);
        group.MapGet("/settings", (IOptions<RetentionOptions> options) => Results.Ok(new { options.Value.Months, options.Value.AutoAnonymize, options.Value.CheckIntervalHours, options.Value.BatchSize }));
        group.MapPost("/run", RunAsync);
        app.MapPost("/api/cvs/{id:guid}/anonymize", AnonymizeAsync).WithTags("CVs").AddEndpointFilter(AuthorizedOperator);
        // A42 is not present: expose only the tombstone guard, not an invented text/quality API.
        app.MapGet("/api/cvs/{id:guid}/text", async (Guid id, SmartCvDbContext db, CancellationToken ct) => await db.Cvs.AnyAsync(c => c.CvId == id && c.AnonymizedAt != null, ct) ? Gone() : Results.NotFound());
        return app;
    }

    public static IResult Gone() => Results.Problem(statusCode: 410, title: "CV đã được ẩn danh hóa", detail: "File gốc và nội dung CV không còn được cung cấp. Điểm và lịch sử quyết định được giữ nguyên.");
    private static async ValueTask<object?> AuthorizedOperator(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.RequestServices.GetService<IAuthenticationSchemeProvider>() is { } schemes && (await schemes.GetAllSchemesAsync()).Any())
        {
            var user = context.HttpContext.User;
            if (user.Identity?.IsAuthenticated != true)
                return Results.Problem(statusCode: 401, title: "Cần đăng nhập để quản lý dữ liệu CV");
            if (!user.IsInRole("HR") && !user.IsInRole("Admin"))
                return Results.Problem(statusCode: 403, title: "Cần quyền HR hoặc Admin");
        }

        return await next(context);
    }

    private static async Task<IResult> AnonymizeAsync(Guid id, CvAnonymizer anonymizer, CancellationToken ct, [FromBody] AnonymizeRequest? request = null)
    {
        var actor = request?.Actor?.Trim() ?? "HR";
        if (actor.Length is 0 or > 100 || actor.Any(char.IsControl))
            return Results.Problem(statusCode: 400, title: "Tên người thực hiện không hợp lệ", detail: "Nhập tối đa 100 ký tự, không chứa ký tự điều khiển.");
        AnonymizeAttempt result;
        try
        {
            result = await anonymizer.AnonymizeAsync(id, actor, false, DateTime.UtcNow, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Problem(statusCode: 500, title: "Không xử lý được CV", detail: "Cần kiểm tra kết nối và trạng thái phục hồi; không tự gửi lại hàng loạt.");
        }

        return result.Outcome switch
        {
            AnonymizeOutcome.NotFound => Results.Problem(statusCode: 404, title: "Không tìm thấy CV"),
            AnonymizeOutcome.Blocked => Results.Problem(statusCode: 409, title: "Chưa ẩn danh hóa được", detail: result.BlockReason),
            AnonymizeOutcome.Failed => Results.Problem(statusCode: 500, title: "Ẩn danh hóa chưa hoàn tất", detail: result.BlockReason),
            _ => Results.Ok(result.Result)
        };
    }

    private static async Task<IResult> RunAsync(RetentionProcessor processor, CancellationToken ct, bool dryRun = true, string? asOf = null)
    {
        var now = DateTime.UtcNow;
        if (asOf is not null)
        {
            var dateOnly = DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            if ((!dateOnly && !asOf.EndsWith('Z') && !asOf.EndsWith("+00:00", StringComparison.Ordinal)) || !DateTimeOffset.TryParse(asOf, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) || value.Offset != TimeSpan.Zero)
                return Results.Problem(statusCode: 400, title: "Thời điểm không hợp lệ", detail: "Dùng ngày yyyy-MM-dd hoặc thời điểm ISO 8601 UTC có Z.");
            now = value.UtcDateTime;
        }

        if (!dryRun && now > DateTime.UtcNow)
            return Results.Problem(statusCode: 400, title: "Không xử lý thật theo ngày tương lai", detail: "Chỉ dùng ngày tương lai khi dryRun=true.");
        try
        {
            return Results.Ok(await processor.RunOnceAsync(now, dryRun, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Problem(statusCode: 500, title: "Không quét được hạn lưu", detail: "Quản trị viên cần kiểm tra kết nối và trạng thái xử lý.");
        }
    }
}
