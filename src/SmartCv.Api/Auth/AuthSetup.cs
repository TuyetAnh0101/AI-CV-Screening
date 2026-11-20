using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Auth;
using SmartCv.Core.Options;

namespace SmartCv.Api.Auth;

public static class AuthPolicies
{
    public const string SignedIn = "SignedIn";
    public const string Admin = "Admin";
}

public static class AuthSetup
{
    public const string LoginRateLimit = "login";
    public const string AccountCredentialRateLimit = "account-credentials";
    public static IServiceCollection AddSmartCvAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>>(new JwtOptionsValidator());
        services.AddOptions<AuthOptions>().Validate(o => o.LoginAttemptsPerMinute is >= 1 and <= 100,
            "Auth:LoginAttemptsPerMinute phải từ 1 đến 100.").ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddSingleton<JwtTokenService>();
        services.AddSingleton<LoginDummyHash>();
        services.AddSingleton<AccountWriteCoordinator>();
        services.AddScoped<UserAccountService>();
        services.AddHttpContextAccessor();
        services.AddScoped<SmartCv.Api.Auditing.AuditTrail>();
        services.AddScoped<SmartCv.Api.Auditing.AuditEndpointFilter>();
        services.AddScoped<ISessionStateStore, DbSessionStateStore>();
        services.AddScoped<UserSessionValidator>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IOptions<JwtOptions>>((bearer, jwt) =>
        {
            bearer.MapInboundClaims = false;
            bearer.IncludeErrorDetails = false;
            bearer.TokenValidationParameters = JwtTokenService.ValidationParameters(jwt.Value);
            bearer.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    try
                    {
                        var validator = context.HttpContext.RequestServices.GetRequiredService<UserSessionValidator>();
                        if (await validator.ValidateAsync(context.Principal!, context.HttpContext.RequestAborted, requireFresh: true) is { } reason) context.Fail(reason);
                    }
                    catch (Exception error) when (AuthProblems.IsDatabaseFailure(error))
                    {
                        context.HttpContext.Items[AuthProblems.UnavailableItem] = true;
                        context.Fail("Không thể kiểm tra tài khoản.");
                        context.HttpContext.RequestServices.GetRequiredService<ILogger<UserSessionValidator>>().LogWarning("Dịch vụ kiểm tra phiên tạm thời không khả dụng.");
                    }
                }
            };
        });
        services.AddAuthorization(o =>
        {
            // Fail closed for newly mapped endpoints, including controllers and routes outside the main group.
            var business = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(UserRoles.Admin, UserRoles.Hr)
                .RequireAssertion(c => !c.User.HasClaim(SmartCvClaims.MustChangePassword, "true")).Build();
            o.DefaultPolicy = business;
            o.FallbackPolicy = business;
            o.AddPolicy(AuthPolicies.Admin, new AuthorizationPolicyBuilder(business).RequireRole(UserRoles.Admin).Build());
            // /me and /change-password remain available while MustChangePassword is true.
            o.AddPolicy(AuthPolicies.SignedIn, policy => policy.RequireAuthenticatedUser().RequireRole(UserRoles.Admin, UserRoles.Hr));
        });
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.AddRateLimiter(o =>
        {
            o.AddPolicy<string, LoginRateLimitPolicy>(LoginRateLimit);
            o.AddPolicy<string, AccountCredentialRateLimitPolicy>(AccountCredentialRateLimit);
        });
        return services;
    }

    public static WebApplication UseSmartCvAuth(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseMiddleware<SmartCv.Api.Auditing.AuditRejectionMiddleware>();
        app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
        return app;
    }

    private sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
    {
        public ValidateOptionsResult Validate(string? name, JwtOptions value) => value.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
