using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using SmartCv.Core.Auth;
using SmartCv.Api.Auditing;

namespace SmartCv.Api.Auth;

/// <summary>OIDC code+PKCE; External is short-lived cookie only. JWT remains default scheme.</summary>
public static class ExternalAuthSetup
{
    public const string CookieScheme = "External";
    public const string GoogleScheme = ExternalProviders.Google;

    public static IServiceCollection AddSmartCvGoogle(this IServiceCollection services, IConfiguration config)
    {
        if (!config.GetValue<bool>("Authentication:Google:Enabled")) return services;
        var id = config["Authentication:Google:ClientId"];
        var secret = config["Authentication:Google:ClientSecret"];
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Google enabled: missing Authentication__Google__ClientId/ClientSecret.");
        var webBase = config["App:WebBaseUrl"];
        if (!Uri.TryCreate(webBase, UriKind.Absolute, out var web) ||
            (web.Scheme != Uri.UriSchemeHttps && !(web.IsLoopback && web.Scheme == Uri.UriSchemeHttp)))
            throw new InvalidOperationException("Google enabled: App:WebBaseUrl must be HTTPS (localhost HTTP only).");

        services.AddAuthentication()
            .AddCookie(CookieScheme, o =>
            {
                o.Cookie.Name = "__Host-SCApp.External";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.Path = "/";
                o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                o.SlidingExpiration = false;
                o.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(GoogleScheme, o =>
            {
                o.Authority = "https://accounts.google.com";
                o.ClientId = id;
                o.ClientSecret = secret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.CallbackPath = "/signin-google";
                o.SignInScheme = CookieScheme;
                o.MapInboundClaims = false;
                o.UsePkce = true;
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = false;
                o.RequireHttpsMetadata = true;
                o.Scope.Clear();
                o.Scope.Add("openid"); o.Scope.Add("email"); o.Scope.Add("profile");
                o.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        context.Response.Headers["Referrer-Policy"] = "no-referrer";
                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = async context =>
                    {
                        var error = context.Failure?.Message;
                        // Error text is not written to logs or sent to the browser.
                        var reason = error is not null && (error.Contains("access_denied", StringComparison.OrdinalIgnoreCase)
                            || error.Contains("cancel", StringComparison.OrdinalIgnoreCase)) ? "cancelled" : "provider_error";
                        context.HandleResponse();
                        await context.HttpContext.RequestServices.GetRequiredService<AuditTrail>().RecordAsync(
                            AuditCatalog.ByCode("auth.external_rejected"), null, "Denied", reason,
                            context.HttpContext.RequestAborted);
                        context.Response.Redirect(ExternalLoginService.WebLoginError(config, reason));
                    },
                    OnAuthenticationFailed = async context =>
                    {
                        context.HandleResponse();
                        await context.HttpContext.RequestServices.GetRequiredService<AuditTrail>().RecordAsync(
                            AuditCatalog.ByCode("auth.external_rejected"), null, "Denied", "provider_error",
                            context.HttpContext.RequestAborted);
                        context.Response.Redirect(ExternalLoginService.WebLoginError(config, "provider_error"));
                    }
                };
            });
        return services;
    }
}
