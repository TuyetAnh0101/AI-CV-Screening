using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace SmartCv.Tests;

/// <summary>
/// A29 incident fail-safe (219 synthetic accounts written to the application catalog during a regression run).
/// Installed for the whole test process: SqlClient reports every connection open and command through its
/// DiagnosticListener synchronously, so a connection or command that targets the application catalog
/// <c>SmartCvDb</c> is refused before the network is used, whichever fixture, factory or configuration
/// source (User Secrets, appsettings, environment) produced the connection string.
/// The only exception is the explicitly opted-in, SELECT-only A28 inspection inside
/// <see cref="AllowReadOnlyApplicationInspection"/>.
/// </summary>
internal static class ApplicationDatabaseGuard
{
    internal const string ApplicationCatalog = "SmartCvDb";
    internal const string Message = "Test process must never open or query the application catalog 'SmartCvDb'. No connection opened.";
    private const string OpenBefore = "Microsoft.Data.SqlClient.WriteConnectionOpenBefore";
    private const string OpenAfter = "Microsoft.Data.SqlClient.WriteConnectionOpenAfter";
    private const string CommandBefore = "Microsoft.Data.SqlClient.WriteCommandBefore";
    private static readonly AsyncLocal<bool> ReadOnlyInspection = new();
    private static int installed;

    [ModuleInitializer]
    internal static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());
    }

    /// <summary>Only for the SELECT-only A28 inspection that is already gated by SMARTCV_A28_INSPECT_APP_DB=1.</summary>
    internal static IDisposable AllowReadOnlyApplicationInspection()
    {
        var previous = ReadOnlyInspection.Value;
        ReadOnlyInspection.Value = true;
        return new Restore(previous);
    }

    internal static bool IsApplicationCatalog(string? catalog) =>
        string.Equals(catalog?.Trim(), ApplicationCatalog, StringComparison.OrdinalIgnoreCase);

    internal static void Check(string? catalog)
    {
        if (IsApplicationCatalog(catalog) && !ReadOnlyInspection.Value) throw new InvalidOperationException(Message);
    }

    private static string? RequestedCatalog(SqlConnection connection)
    {
        try { return new SqlConnectionStringBuilder(connection.ConnectionString).InitialCatalog; }
        catch (ArgumentException) { return connection.Database; }
    }

    private static T? Property<T>(object? payload, string name) where T : class =>
        payload?.GetType().GetProperty(name)?.GetValue(payload) as T;

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => ReadOnlyInspection.Value = previous;
    }

    private sealed class ListenerObserver : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "SqlClientDiagnosticListener")
                listener.Subscribe(new EventObserver(), (name, _, _) => name is OpenBefore or OpenAfter or CommandBefore);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class EventObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> e)
        {
            switch (e.Key)
            {
                case OpenBefore when Property<SqlConnection>(e.Value, "Connection") is { } connection:
                    Check(RequestedCatalog(connection)); break;
                case OpenAfter when Property<SqlConnection>(e.Value, "Connection") is { } connection:
                    Check(connection.Database); break; // e.g. an empty Initial Catalog resolving to the login default
                case CommandBefore when Property<SqlCommand>(e.Value, "Command")?.Connection is { } connection:
                    Check(connection.Database); break; // e.g. a script that switches with USE SmartCvDb
            }
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
