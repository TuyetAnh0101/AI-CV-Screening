using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Routing;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Parsing;
using SmartCv.Core.Privacy;

namespace SmartCv.Api.Ops;

public static class LoggingSetup
{
    public static void AddOperationalLogging(this WebApplicationBuilder builder)
    {
        // One pipeline, no global Log.Logger and no unredacted Console/EventLog provider alongside it.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILoggerProvider>(_ => new SerilogLoggerProvider(CreateLogger(
            builder.Configuration, builder.Environment), dispose: true));
    }
    public static Logger CreateLogger(IConfiguration config, IHostEnvironment env)
    {
        var destination = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Console(
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}");
        if (config.GetValue("Logging:File:Enabled", !env.IsDevelopment()))
        {
            var days = config.GetValue("Logging:File:RetainDays", 30);
            if (days is < 1 or > 365) throw new InvalidOperationException("Logging:File:RetainDays phải từ 1 đến 365.");
            var root = config["Logging:File:Directory"];
            var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? "logs" : root, env.ContentRootPath);
            try
            {
                Directory.CreateDirectory(directory);
                using var probe = new FileStream(Path.Combine(directory, ".log-probe-" + Guid.NewGuid().ToString("N")),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                probe.WriteByte(1);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException("Không ghi được thư mục log đã cấu hình. Kiểm tra quyền của tiến trình API."); }
            destination.WriteTo.File(Path.Combine(directory, "smartcv-api-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: days, retainedFileTimeLimit: TimeSpan.FromDays(days), shared: true,
                fileSizeLimitBytes: 20 * 1024 * 1024, rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}");
        }
        return new LoggerConfiguration().MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext().WriteTo.Sink(new RedactingLogSink(destination.CreateLogger())).CreateLogger();
    }

    public static void UseOperationalRequestLogging(this WebApplication app) => app.Use(async (http, next) =>
    {
        var started = Stopwatch.GetTimestamp();
        try { await next(http); }
        finally
        {
            // Endpoint template only: never the incoming path, query, headers, actor name or request body.
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SCApp.Requests").LogInformation(
                "HTTP {Method} {Route} returned {StatusCode} in {Elapsed} ms; request {RequestId}",
                http.Request.Method, route, http.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, http.TraceIdentifier);
        }
    });
}

/// <summary>Rebuild events before every sink. Exceptions, scopes, structured values and template literals are covered.</summary>
public sealed class RedactingLogSink(Serilog.ILogger destination) : ILogEventSink, IDisposable
{
    private static readonly HashSet<string> SafeTokens = new(StringComparer.OrdinalIgnoreCase)
        { "SourceContext", "Action", "ActionCode", "ResourceType", "RequestId", "TraceId", "SpanId", "Type", "ErrorType",
          "FailureType", "Status", "Code", "Format", "Method", "Model", "Lang", "Migrations", "Route", "Environment",
          "SqlNumber", "SqlErrorNumber", "UserAccess", "Kind", "Source" };
    private static readonly MessageTemplateParser Parser = new();
    public void Emit(LogEvent value)
    {
        var properties = value.Properties.Select(p => new LogEventProperty(p.Key, Mask(p.Key, p.Value))).ToList();
        if (value.Exception is { } error) properties.Add(new("FailureType", new ScalarValue(error.GetType().Name)));
        var safe = new LogEvent(value.Timestamp, value.Level, null, Parser.Parse(LogMasking.MaskText(value.MessageTemplate.Text)), properties);
        destination.Write(safe);
    }
    private static LogEventPropertyValue Mask(string name, LogEventPropertyValue value)
    {
        if (LogMasking.IsSensitiveName(name) && !SafeTokens.Contains(name)) return new ScalarValue(LogMasking.Hidden);
        return value switch
        {
            ScalarValue { Value: string text } => new ScalarValue(SafeTokens.Contains(name) && SafeToken(
                name.Equals("Migrations", StringComparison.OrdinalIgnoreCase) ? text.Replace(" ", "") : text) ? text : LogMasking.Hidden),
            ScalarValue { Value: null or bool or byte or short or int or long or float or double or decimal or Guid or DateTime or DateTimeOffset or TimeSpan } => value,
            // Arbitrary objects, arrays, dictionary keys and type tags can carry private CV data.
            _ => new ScalarValue(LogMasking.Hidden)
        };
    }
    private static bool SafeToken(string value) => value.Length is > 0 and <= 200
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/' or '{' or '}' or ',');
    public void Dispose() => (destination as IDisposable)?.Dispose();
}
