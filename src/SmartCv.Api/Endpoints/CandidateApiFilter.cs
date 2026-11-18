using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SmartCv.Api.Endpoints;

/// <summary>A24 errors contain no contact details, CV text, paths, SQL or raw exception messages.</summary>
public sealed class CandidateApiFilter(ILogger<CandidateApiFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (Exception ex) when (context.HttpContext.RequestAborted.IsCancellationRequested
            && (ex is OperationCanceledException || Sql(ex) is not null))
        {
            throw new OperationCanceledException("Candidate request cancelled.", ex, context.HttpContext.RequestAborted);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: 409, title: "Dữ liệu đã thay đổi",
                detail: "Hãy tải lại hồ sơ trước khi quyết định thao tác tiếp.");
        }
        catch (Exception ex) when (ex is TimeoutException || Sql(ex) is { Number: -2 or 1205 or 1222 or 51024 })
        {
            logger.LogWarning("A24 database operation unavailable ({ErrorType}).", ex.GetType().Name);
            return Results.Problem(statusCode: 503, title: "Chưa hoàn tất thao tác ứng viên",
                detail: HttpMethods.IsGet(context.HttpContext.Request.Method)
                    ? "Kết nối hoặc dữ liệu đang bận. Hãy tải lại sau."
                    : "Hãy tải lại để kiểm tra trạng thái trước khi gửi thao tác tiếp. Không tự gửi lại thao tác ghi.");
        }
        catch (DbUpdateException ex) when (Sql(ex) is { Number: 547 or 2601 or 2627 })
        {
            return Results.Problem(statusCode: 409, title: "Dữ liệu liên quan đã thay đổi",
                detail: "Hãy tải lại hồ sơ trước khi quyết định thao tác tiếp.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("A24 operation failed ({ErrorType}).", ex.GetType().Name);
            return Results.Problem(statusCode: 500, title: "Chưa hoàn tất thao tác ứng viên",
                detail: "Hãy tải lại để kiểm tra trạng thái. Liên hệ quản trị viên nếu lỗi tiếp diễn.");
        }
    }

    private static SqlException? Sql(Exception error) =>
        error as SqlException ?? (error.InnerException is { } inner ? Sql(inner) : null);
}
