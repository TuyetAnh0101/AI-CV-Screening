using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Api.Middleware;

namespace SmartCv.Tests;

public class SqlReadCancellationTests
{
    [Theory]
    [InlineData("GET", true, "cancel", true)]
    [InlineData("HEAD", true, "cancel", true)]
    [InlineData("GET", true, "aborted-batch-with-cancel", true)]
    [InlineData("GET", true, "wrapped-cancel", true)]
    [InlineData("GET", true, "driver-reader-cancel", true)]
    [InlineData("GET", false, "driver-reader-cancel", false)]
    [InlineData("GET", false, "cancel", false)]
    [InlineData("POST", true, "cancel", false)]
    [InlineData("PATCH", true, "cancel", false)]
    [InlineData("GET", true, "missing-column", false)]
    [InlineData("GET", true, "mixed-schema-and-cancel", false)]
    [InlineData("GET", true, "timeout", false)]
    [InlineData("GET", true, "aborted-batch-alone", false)]
    [InlineData("GET", true, "unrelated", false)]
    [InlineData("GET", true, "already-cancelled", false)]
    public async Task Only_aborted_SQL_reads_are_normalized_and_real_failures_are_preserved(
        string method, bool aborted, string scenario, bool normalize)
    {
        using var cancellation = new CancellationTokenSource();
        if (aborted) cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Request.Method = method;
        Exception failure = scenario switch
        {
            "missing-column" => SqlError((207, 16, 1)),
            "mixed-schema-and-cancel" => SqlError((207, 16, 1), (0, 11, 0)),
            "timeout" => SqlError((-2, 11, 0)),
            "aborted-batch-alone" => SqlError((3980, 16, 1)),
            "aborted-batch-with-cancel" => new InvalidOperationException("EF execution strategy", SqlError((3980, 16, 1), (0, 11, 0))),
            "wrapped-cancel" => new InvalidOperationException("EF execution strategy", SqlError((0, 11, 0), (0, 11, 0))),
            "driver-reader-cancel" => DriverReaderCancellation(),
            "unrelated" => new InvalidOperationException("Unrelated failure"),
            "already-cancelled" => new OperationCanceledException(cancellation.Token),
            _ => SqlError((0, 11, 0), (0, 11, 0))
        };
        var calls = 0;
        var middleware = new SqlReadCancellationMiddleware(_ =>
        {
            calls++;
            return Task.FromException(failure);
        }, NullLogger<SqlReadCancellationMiddleware>.Instance);

        if (normalize)
        {
            var result = await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
            Assert.Same(failure, result.InnerException);
            Assert.Equal(cancellation.Token, result.CancellationToken);
        }
        else
            Assert.Same(failure, await Record.ExceptionAsync(() => middleware.InvokeAsync(context)));
        Assert.Equal(1, calls); // No read or write is ever retried.
    }

    private static Exception DriverReaderCancellation()
    {
        // Replay the installed driver's reader continuation for a faulted cancellation task, preserving its real origin.
        var continuationType = typeof(SqlCommand).GetNestedType("<>c", BindingFlags.NonPublic)!;
        var continuation = continuationType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name.Contains("<ExecuteDbDataReaderAsync>", StringComparison.Ordinal)
                && m.GetParameters() is [{ ParameterType: var type }] && type == typeof(Task<SqlDataReader>));
        var instance = Activator.CreateInstance(continuationType, nonPublic: true);
        var thrown = Assert.Throws<TargetInvocationException>(() => continuation.Invoke(instance,
            [Task.FromException<SqlDataReader>(new InvalidOperationException("Synthetic driver cancellation"))]));
        return Assert.IsType<InvalidOperationException>(thrown.InnerException);
    }

    private static SqlException SqlError(params (int Number, byte Severity, byte State)[] details)
    {
        // SqlClient exposes no public constructors. Synthetic diagnostics make the cancellation and real-error guards
        // deterministic; separate SQL/browser tests exercise the actual driver and request-abort behavior.
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var add = typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var constructor = typeof(SqlError).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)], null)!;
        foreach (var (number, severity, state) in details)
            add.Invoke(collection, [constructor.Invoke([number, state, severity, "synthetic-server", "Lỗi đã bản địa hóa", "", 1, null])]);
        var create = typeof(SqlException).GetMethod("CreateException", BindingFlags.Static | BindingFlags.NonPublic, null,
            [typeof(SqlErrorCollection), typeof(string)], null)!;
        return (SqlException)create.Invoke(null, [collection, "16.0"])!;
    }
}
