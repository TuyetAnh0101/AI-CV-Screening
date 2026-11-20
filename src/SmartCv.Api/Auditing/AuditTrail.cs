using System.Globalization;
using System.Security.Claims;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Auditing;

/// <summary>No payload APIs. Atomic callers stage an event on their existing context and confirm only after commit.</summary>
public sealed class AuditTrail(IHttpContextAccessor accessor, IServiceScopeFactory scopes, TimeProvider time, ILogger<AuditTrail> logger)
{
    private const string RecordedKey = "B43.Audit.Recorded";
    public bool IsRecorded => accessor.HttpContext?.Items.ContainsKey(RecordedKey) == true;
    public void ConfirmCommitted() { if (accessor.HttpContext is { } http) http.Items[RecordedKey] = true; }

    public AuditEvent Stage(SmartCvDbContext db, string code, string? resourceId, ClaimsPrincipal? actor = null,
        User? credentialVerifiedUser = null, string outcome = "Succeeded")
    {
        var row = Create(AuditCatalog.ByCode(code), resourceId, outcome, null, actor, credentialVerifiedUser);
        db.AuditEvents.Add(row);
        return row;
    }

    public async Task<bool> RecordAsync(AuditAction action, string? resourceId, string outcome, string? failureCode, CancellationToken ct)
    {
        if (IsRecorded) return true;
        // A failed business SaveChanges may leave tracked mutations: never reuse that context to write a failure event.
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var row = Create(action, resourceId, outcome, failureCode);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            db.AuditEvents.Add(row);
            await db.SaveChangesAsync(deadline.Token);
            ConfirmCommitted();
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // No exception/body/URL/identity/credential values; operational monitoring must alert on this event.
            LogUnavailable(logger, action.Code, RequestId(), error);
            return false;
        }
    }

    /// <summary>Shared critical log for every failed audit write (staged or standalone). Only the failure class,
    /// SQL error number and a schema hint are logged; never the exception message, payload or identity.</summary>
    internal static void LogUnavailable(ILogger logger, string actionCode, string requestId, Exception error)
    {
        var sqlNumber = AuthProblems.SqlErrorNumber(error);
        if (AuthProblems.IsSchemaMismatch(sqlNumber))
            logger.LogCritical("Audit persistence unavailable for {ActionCode}; request {RequestId}; failure type {Type}; SQL error {SqlNumber}: " +
                "database schema is missing AuditEvents. Apply migration 20261120043759_AddAuditEvents (database/b43_add_audit_events.sql).",
                actionCode, requestId, error.GetType().Name, sqlNumber);
        else
            logger.LogCritical("Audit persistence unavailable for {ActionCode}; request {RequestId}; failure type {Type}; SQL error {SqlNumber}.",
                actionCode, requestId, error.GetType().Name, sqlNumber?.ToString(CultureInfo.InvariantCulture) ?? "none");
    }

    private AuditEvent Create(AuditAction action, string? resourceId, string outcome, string? failure,
        ClaimsPrincipal? actor = null, User? verified = null)
    {
        actor ??= accessor.HttpContext?.User;
        var trusted = actor?.Identity?.IsAuthenticated == true && UserRoles.IsValid(actor.GetRole()) && actor.GetUserId() is not null;
        var id = verified?.UserId ?? (trusted ? actor!.GetUserId() : null);
        var name = verified?.FullName ?? (trusted ? actor!.FindFirstValue(SmartCvClaims.Name) : null);
        var role = verified?.Role ?? (trusted ? actor!.GetRole() : null);
        if (id is null || string.IsNullOrWhiteSpace(name) || !UserRoles.IsValid(role)) { id = null; name = role = null; }
        else name = new string(name.Where(c => !char.IsControl(c)).Take(150).ToArray());
        return new AuditEvent
        {
            OccurredAtUtc = time.GetUtcNow().UtcDateTime, ActorUserId = id,
            ActorNameSnapshot = name, ActorRoleSnapshot = role, ActionCode = action.Code, Category = action.Category,
            ResourceType = action.ResourceType, ResourceId = SafeResource(resourceId), Outcome = outcome,
            RequestId = RequestId(), SafeSummary = action.Summary, FailureCode = failure
        };
    }
    private string RequestId()
    {
        var http = accessor.HttpContext;
        if (http is null) return Guid.NewGuid().ToString("N");
        var value = http.TraceIdentifier;
        if (value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_')) return value;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }
    public static string? SafeResource(string? value) => Guid.TryParse(value, out var guid) ? guid.ToString("D")
        : long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : null;
}
