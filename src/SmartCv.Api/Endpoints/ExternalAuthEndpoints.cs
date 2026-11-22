using Microsoft.AspNetCore.Authentication;
using SmartCv.Api.Auth;
using SmartCv.Api.Auditing;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Endpoints;

public static class ExternalAuthEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/external/{provider}/start", StartAsync).AllowAnonymous()
            .RequireRateLimiting(AuthSetup.ExternalStartRateLimit).WithName("ExternalStart");
        group.MapGet("/external/{provider}/complete", CompleteAsync).AllowAnonymous()
            .RequireRateLimiting(AuthSetup.ExternalStartRateLimit).WithName("ExternalComplete");
        group.MapPost("/external/exchange", ExchangeAsync).AllowAnonymous()
            .RequireRateLimiting(AuthSetup.ExternalExchangeRateLimit).WithName("ExternalExchange");
        group.MapPost("/external/link-ticket", LinkTicketAsync)
            .RequireAuthorization(AuthPolicies.SignedIn)
            .RequireRateLimiting(AuthSetup.AccountCredentialRateLimit).WithName("ExternalLinkTicket");
        group.MapGet("/me/logins", LoginsAsync)
            .RequireAuthorization(AuthPolicies.SignedIn).WithName("GetLinkedLogins");
        group.MapDelete("/me/logins/{provider}", UnlinkAsync)
            .RequireAuthorization(AuthPolicies.SignedIn)
            .RequireRateLimiting(AuthSetup.AccountCredentialRateLimit).WithName("UnlinkExternalLogin");
    }

    private static async Task<IResult> StartAsync(string provider, HttpContext http,
        ExternalLoginService service, CancellationToken ct)
    {
        if (provider != ExternalProviders.Google || !service.Enabled)
            return Results.NotFound();
        var query = http.Request.Query;
        if (query["intent"].Count > 1 || query["returnUrl"].Count > 1 || query["ticket"].Count > 1)
            return AuthProblems.Problem(400, "invalid_request", "Yêu cầu không hợp lệ", "Thông tin đăng nhập bị trùng.");
        var intent = query["intent"].ToString();
        if (string.IsNullOrEmpty(intent)) intent = "login";
        if (intent is not ("login" or "link"))
            return AuthProblems.Problem(400, "invalid_request", "Yêu cầu không hợp lệ", "Intent phải là login hoặc link.");
        var rawTicket = query["ticket"].ToString();
        if (intent == "link" && !await service.IsValidLinkTicketAsync(rawTicket, ct))
            return AuthProblems.Problem(400, "token_invalid", "Không thể liên kết", "Vé liên kết không hợp lệ hoặc đã hết hạn.");
        if (intent == "login" && !string.IsNullOrEmpty(rawTicket))
            return AuthProblems.Problem(400, "invalid_request", "Yêu cầu không hợp lệ", "Vé liên kết không dùng với login.");

        var props = new AuthenticationProperties { RedirectUri = "/api/auth/external/google/complete",
            IsPersistent = false, AllowRefresh = false };
        props.Items[ExternalLoginService.FlowIntent] = intent;
        props.Items[ExternalLoginService.FlowProvider] = provider;
        props.Items[ExternalLoginService.FlowReturn] = ExternalProviders.SafeReturnUrl(query["returnUrl"].ToString());
        if (intent == "link") props.Items[ExternalLoginService.FlowTicket] = rawTicket;
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers.CacheControl = "no-store";
        return Results.Challenge(props, [ExternalAuthSetup.GoogleScheme]);
    }

    private static async Task<IResult> CompleteAsync(string provider, HttpContext http,
        ExternalLoginService service, IConfiguration config, CancellationToken ct)
    {
        if (provider != ExternalProviders.Google || !service.Enabled) return Results.NotFound();
        var auth = await http.AuthenticateAsync(ExternalAuthSetup.CookieScheme);
        if (!auth.Succeeded || auth.Principal?.Identity?.IsAuthenticated != true || auth.Properties is null)
        {
            await http.RequestServices.GetRequiredService<AuditTrail>().RecordAsync(
                AuditCatalog.ByCode("auth.external_rejected"), null, "Denied", "provider_error", ct);
            return Results.Redirect(ExternalLoginService.WebLoginError(config, "provider_error"));
        }
        var properties = auth.Properties.Items;
        properties.TryGetValue(ExternalLoginService.FlowIntent, out var intent);
        properties.TryGetValue(ExternalLoginService.FlowProvider, out var boundProvider);
        properties.TryGetValue(ExternalLoginService.FlowTicket, out var ticket);
        properties.TryGetValue(ExternalLoginService.FlowReturn, out var returnUrl);
        try
        {
            if (boundProvider != provider) return Results.Redirect(ExternalLoginService.WebLoginError(config, "provider_error"));
            var answer = await service.CompleteAsync(auth.Principal, provider, intent ?? "", ticket, returnUrl, ct);
            return Results.Redirect(answer.RedirectUrl);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            // No exception text, credential, email or provider subject in the response or log.
            var logger = http.RequestServices.GetRequiredService<ILogger<ExternalAuthEndpointsMarker>>();
            logger.LogWarning("External complete failed. Type={Type} SqlNumber={Number}", error.GetType().Name, AuthProblems.SqlErrorNumber(error));
            return Results.Redirect(ExternalLoginService.WebLoginError(config, "provider_error"));
        }
        finally { await http.SignOutAsync(ExternalAuthSetup.CookieScheme); }
    }

    private sealed class ExternalAuthEndpointsMarker { }

    private static Task<IResult> ExchangeAsync(HttpContext http, ExternalLoginService service,
        ILogger<ExternalExchangeRequest> logger, CancellationToken ct) => AccountApiSupport.RunAsync(http, logger, async () =>
    {
        var request = await AccountApiSupport.ReadAsync<ExternalExchangeRequest>(http.Request, ct);
        var result = await service.ExchangeAsync(request.Code, ct);
        return Results.Ok(new ExternalExchangeResponse(result.Login.Token, result.Login.ExpiresAt,
            result.Login.User, result.Login.IsNewWorkspace, result.ReturnUrl));
    });

    private static Task<IResult> LinkTicketAsync(HttpContext http, ExternalLoginService service,
        ILogger<ExternalLinkTicketRequest> logger, CancellationToken ct) => AccountApiSupport.RunAsync(http, logger, async () =>
    {
        var request = await AccountApiSupport.ReadAsync<ExternalLinkTicketRequest>(http.Request, ct);
        if (request.Provider != ExternalProviders.Google)
            throw new AccountFailure(400, "provider_disabled", "Provider không hỗ trợ.");
        var url = await service.CreateLinkStartUrlAsync(http.User, request.Provider, ct);
        return Results.Ok(new ExternalStartUrl(url));
    });

    private static Task<IResult> LoginsAsync(HttpContext http, ExternalLoginService service,
        ILogger<LinkedLoginDto> logger, CancellationToken ct) => AccountApiSupport.RunAsync(http, logger, async () =>
        Results.Ok(await service.GetLoginsAsync(http.User, ct)));

    private static Task<IResult> UnlinkAsync(string provider, HttpContext http, ExternalLoginService service,
        ILogger<ExternalAuthEndpointsMarker> logger, CancellationToken ct) => AccountApiSupport.RunAsync(http, logger, async () =>
    {
        await service.UnlinkAsync(http.User, provider, ct);
        return Results.NoContent();
    });
}

public sealed class ExternalExchangeRequest { public string? Code { get; set; } }
public sealed class ExternalLinkTicketRequest { public string? Provider { get; set; } }
public sealed record ExternalExchangeResponse(string Token, DateTime ExpiresAt, MeDto User, bool IsNewWorkspace, string ReturnUrl);
