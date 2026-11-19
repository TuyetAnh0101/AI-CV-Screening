using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Options;

namespace SmartCv.Api.Auth;

public static class AuthPolicies
{
    public const string SignedIn = "SignedIn";
}

public static class AuthSetup
{
    public const string LoginRateLimit = "login";
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
                        if (await validator.ValidateAsync(context.Principal!, context.HttpContext.RequestAborted) is { } reason) context.Fail(reason);
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
        services.AddAuthorization(o => o.AddPolicy(AuthPolicies.SignedIn, policy => policy.RequireAuthenticatedUser()));
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.AddRateLimiter(o => o.AddPolicy<string, LoginRateLimitPolicy>(LoginRateLimit));
        return services;
    }

    public static WebApplication UseSmartCvAuth(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/auth")) context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
        return app;
    }

    private sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
    {
        public ValidateOptionsResult Validate(string? name, JwtOptions value) => value.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
