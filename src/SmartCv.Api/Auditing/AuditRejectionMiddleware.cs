namespace SmartCv.Api.Auditing;

/// <summary>Authorization/rate-limit failures occur before endpoint filters. Never parse an unverified token.</summary>
public sealed class AuditRejectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, AuditTrail audit)
    {
        await next(http);
        if (audit.IsRecorded || http.Response.StatusCode is not (401 or 403 or 429)) return;
        var action = AuditCatalog.For(http);
        if (action is null && http.Response.StatusCode == 401 && http.Request.Headers.ContainsKey("Authorization"))
            action = AuditCatalog.ByCode("auth.session_rejected");
        if (action is null) return;
        await audit.RecordAsync(action, AuditEndpointFilter.RouteResource(http, action), "Denied",
            http.Response.StatusCode == 429 ? "rate_limited" : http.Response.StatusCode == 403 ? "access_denied" : "invalid_session", CancellationToken.None);
    }
}
