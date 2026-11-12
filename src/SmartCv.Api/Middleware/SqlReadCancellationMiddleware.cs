using Microsoft.Data.SqlClient;

namespace SmartCv.Api.Middleware;

/// <summary>SqlClient can surface a cancelled SQL read as SqlException rather than OperationCanceledException.</summary>
public sealed class SqlReadCancellationMiddleware(RequestDelegate next, ILogger<SqlReadCancellationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (Exception ex) when ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && context.RequestAborted.IsCancellationRequested && IsSqlCancellation(ex))
        {
            logger.LogDebug("Database read cancelled after request was aborted: {Path}", context.Request.Path);
            // Preserve the cause and the caller's token. ASP.NET Core handles an aborted request without a false 500.
            // Never retry a request or turn a database failure into a successful response.
            throw new OperationCanceledException("Database read cancelled with the HTTP request.", ex, context.RequestAborted);
        }
    }

    internal static bool IsSqlCancellation(Exception error)
    {
        while (error is not SqlException && error.InnerException is not null) error = error.InnerException;
        // SqlCommand's async reader continuation also surfaces InvalidOperationException for cancellation.
        // Match its driver call site instead of a localized message; unrelated InvalidOperationExceptions propagate.
        var origin = error.TargetSite?.DeclaringType;
        if (error is InvalidOperationException && origin?.Assembly == typeof(SqlCommand).Assembly
            && origin.DeclaringType == typeof(SqlCommand)
            && error.TargetSite!.Name.StartsWith("<ExecuteDbDataReaderAsync>", StringComparison.Ordinal)) return true;
        if (error is not SqlException sql || sql.Errors.Count == 0) return false;
        // Cancellation diagnostics have no dedicated error number. Use the observed driver signature, never localized text.
        // A transaction batch-aborted error (3980) is accepted only alongside the cancellation diagnostic.
        var hasCancellation = false;
        foreach (SqlError detail in sql.Errors)
        {
            if (detail is { Number: 0, Class: 11, State: 0 }) hasCancellation = true;
            else if (detail.Number != 3980) return false;
        }
        return hasCancellation;
    }
}
