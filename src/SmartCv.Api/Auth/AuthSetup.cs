using SmartCv.Infrastructure.Auth;
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
    // Existing routes use Admin: keep the alias during migration.
    public const string Admin = "Admin";
    public const string SystemAdmin = "SystemAdmin";
    public const string BusinessOperator = "BusinessOperator";
    public const string WorkspaceOwner = "WorkspaceOwner";
}

public static class AuthSetup
{
    public const string LoginRateLimit = "login";
    public const string AccountCredentialRateLimit = "account-credentials";
    public const string PublicRegisterLimit = "public-register";
    public const string PublicVerifyLimit = "public-verify";
    public const string PublicResendLimit = "public-resend";
    public const string ForgotPasswordRateLimit = "forgot-password";
    public const string ExternalStartRateLimit = "external-start";
    public const string ExternalExchangeRateLimit = "external-exchange";
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
        services.AddScoped<UserTokenService>();
        services.AddScoped<RegistrationService>();
        services.AddScoped<AccountSelfServiceService>();
        services.AddScoped<ExternalLoginService>();
        services.AddHttpContextAccessor();
        services.AddScoped<SmartCv.Api.Auditing.AuditTrail>();
        services.AddScoped<SmartCv.Api.Auditing.AuditEndpointFilter>();
        services.AddScoped<SmartCv.Core.Workspaces.IWorkspaceContext, HttpWorkspaceContext>();
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
                        var session = await validator.ValidateSessionAsync(context.Principal!, context.HttpContext.RequestAborted, requireFresh: true);
                        if (session.Error is { } reason) context.Fail(reason);
                        // A52: chỉ workspace đã xác nhận với database mới được đưa vào ngữ cảnh request.
                        else if (session.Workspace is { } workspace) context.HttpContext.Items[HttpWorkspaceContext.ItemKey] = workspace;
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
            // A01: dùng cùng một điều kiện tài khoản hợp lệ, nhưng KHÔNG buộc Admin
            // có workspace để sử dụng API quản trị. Nguồn quyền là DB-validated JWT.
            var staff = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(UserRoles.Admin, UserRoles.Hr)
                .RequireAssertion(c => !c.User.HasClaim(SmartCvClaims.MustChangePassword, "true"))
                .Build();
            var systemAdmin = new AuthorizationPolicyBuilder(staff)
                .RequireRole(UserRoles.Admin).Build();
            var business = new AuthorizationPolicyBuilder(staff)
                .RequireRole(UserRoles.Hr)
                .RequireAssertion(c => c.Resource is HttpContext http &&
                    RoleBoundaryRules.CanOperate(c.User, HttpWorkspaceContext.Get(http)))
                .Build();
            var owner = new AuthorizationPolicyBuilder(business)
                .RequireAssertion(c => c.Resource is HttpContext http &&
                    RoleBoundaryRules.IsPersonalOwner(c.User, HttpWorkspaceContext.Get(http)))
                .Build();
            // Routes chưa phân loại không mặc định trao quyền Admin; endpoint business
            // luôn nằm trong API group BusinessOperator ở SmartCvApi.cs.
            o.DefaultPolicy = business;
            o.FallbackPolicy = business;
            o.AddPolicy(AuthPolicies.SystemAdmin, systemAdmin);
            o.AddPolicy(AuthPolicies.Admin, systemAdmin); // compatibility for existing Admin routes
            o.AddPolicy(AuthPolicies.BusinessOperator, business);
            o.AddPolicy(AuthPolicies.WorkspaceOwner, owner);
            // /me, /change-password và self-service không yêu cầu workspace hoặc đổi mật khẩu trước.
            o.AddPolicy(AuthPolicies.SignedIn,
                policy => policy.RequireAuthenticatedUser().RequireRole(UserRoles.Admin, UserRoles.Hr));
        });
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.AddRateLimiter(o =>
        {
            o.AddPolicy<string, LoginRateLimitPolicy>(LoginRateLimit);
            o.AddPolicy<string, AccountCredentialRateLimitPolicy>(AccountCredentialRateLimit);
            o.AddPolicy<string, RegisterRateLimitPolicy>(PublicRegisterLimit);
            o.AddPolicy<string, VerifyEmailRateLimitPolicy>(PublicVerifyLimit);
            o.AddPolicy<string, ResendVerificationRateLimitPolicy>(PublicResendLimit);
            o.AddPolicy<string, ForgotPasswordRateLimitPolicy>(ForgotPasswordRateLimit);
            o.AddPolicy<string, ExternalStartRateLimitPolicy>(ExternalStartRateLimit);
            o.AddPolicy<string, ExternalExchangeRateLimitPolicy>(ExternalExchangeRateLimit);
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
        SmartCv.Infrastructure.Workspaces.WorkspaceScope.UseLogger(app.Services.GetRequiredService<ILoggerFactory>());
        app.UseMiddleware<SmartCv.Api.Auditing.AuditRejectionMiddleware>();
        app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
        return app;
    }

    private sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
    {
        public ValidateOptionsResult Validate(string? name, JwtOptions value) => value.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
