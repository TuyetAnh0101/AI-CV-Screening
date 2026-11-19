using System.Globalization;
using System.Security.Claims;
using SmartCv.Api.Auth;
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
    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled)
            return app; // Feature flag retained; MapSmartCvApi and Admin policies enforce authorization.
        var group = app.MapGroup("/api/retention").WithTags("Retention");
        group.MapGet("/settings", (IOptions<RetentionOptions> options) => Results.Ok(new { options.Value.Months, options.Value.AutoAnonymize, options.Value.CheckIntervalHours, options.Value.BatchSize }));
        group.MapPost("/run", RunAsync).RequireAuthorization(AuthPolicies.Admin);
        app.MapPost("/api/cvs/{id:guid}/anonymize", AnonymizeAsync).WithTags("CVs").RequireAuthorization(AuthPolicies.Admin);
        // A42: GET /api/cvs/{id}/text (chữ đã đọc + chất lượng) nằm ở CvEndpoints và vẫn trả 410 cho CV đã ẩn danh hóa.
        return app;
    }

    public static IResult Gone() => Results.Problem(statusCode: 410, title: "CV đã được ẩn danh hóa", detail: "File gốc và nội dung CV không còn được cung cấp. Điểm và lịch sử quyết định được giữ nguyên.");

    private static async Task<IResult> AnonymizeAsync(Guid id, ClaimsPrincipal user, CvAnonymizer anonymizer, CancellationToken ct, [FromBody] AnonymizeRequest? request = null)
    {
        var actor = user.ActorName();
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

    private static async Task<IResult> RunAsync(RetentionProcessor processor, ClaimsPrincipal user, ILogger<RetentionProcessor> logger, CancellationToken ct, bool dryRun = true, string? asOf = null)
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
            var result = await processor.RunOnceAsync(now, dryRun, ct);
            logger.LogInformation("Retention run by user {UserId}; dry run {DryRun}", user.GetUserId(), dryRun);
            return Results.Ok(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Problem(statusCode: 500, title: "Không quét được hạn lưu", detail: "Quản trị viên cần kiểm tra kết nối và trạng thái xử lý.");
        }
    }
}
