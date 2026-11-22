using SmartCv.Api.Auth;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Endpoints;

/// <summary>Chỉ API A57; UI được thực hiện tại B54/B55.</summary>
public static class AccountSelfServiceEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/forgot-password", ForgotAsync)
            .AllowAnonymous().RequireRateLimiting(AuthSetup.ForgotPasswordRateLimit)
            .WithName("ForgotPassword").Produces<AccountNotice>(202);

        group.MapPost("/reset-password", ResetAsync)
            .AllowAnonymous().RequireRateLimiting(AuthSetup.PublicVerifyLimit)
            .WithName("ResetPassword").Produces<AccountNotice>();

        group.MapGet("/me/profile", ProfileAsync)
            .RequireAuthorization(AuthPolicies.SignedIn)
            .WithName("GetMyProfile").Produces<ProfileDto>();

        group.MapPut("/me/profile", UpdateProfileAsync)
            .RequireAuthorization(AuthPolicies.SignedIn)
            .RequireRateLimiting(AuthSetup.AccountCredentialRateLimit)
            .WithName("UpdateMyProfile").Produces<ProfileDto>();

        group.MapPost("/me/password-setup", SetupPasswordAsync)
            .RequireAuthorization(AuthPolicies.SignedIn)
            .RequireRateLimiting(AuthSetup.AccountCredentialRateLimit)
            .WithName("RequestPasswordSetup").Produces<AccountNotice>(202);
    }

    private static Task<IResult> ForgotAsync(HttpContext http, AccountSelfServiceService service,
        ILogger<ForgotPasswordRequest> logger, CancellationToken ct) =>
        AccountApiSupport.RunAsync(http, logger, async () =>
        {
            var input = await AccountApiSupport.ReadAsync<ForgotPasswordRequest>(http.Request, ct);
            await service.ForgotAsync(input.Email, ct);
            return Results.Accepted(value: AccountSelfServiceService.GenericForgotNotice);
        });

    private static Task<IResult> ResetAsync(HttpContext http, AccountSelfServiceService service,
        ILogger<ResetPasswordRequest> logger, CancellationToken ct) =>
        AccountApiSupport.RunAsync(http, logger, async () =>
        {
            var input = await AccountApiSupport.ReadAsync<ResetPasswordRequest>(http.Request, ct);
            await service.ResetAsync(input.Token, input.NewPassword, ct);
            return Results.Ok(new AccountNotice("Mật khẩu đã được đặt lại. Vui lòng đăng nhập bằng mật khẩu mới."));
        });

    private static Task<IResult> ProfileAsync(HttpContext http, AccountSelfServiceService service,
        ILogger<ProfileDto> logger, CancellationToken ct) =>
        AccountApiSupport.RunAsync(http, logger, async () => Results.Ok(await service.GetProfileAsync(http.User, http, ct)));

    private static Task<IResult> UpdateProfileAsync(HttpContext http, AccountSelfServiceService service,
        ILogger<UpdateMyProfileRequest> logger, CancellationToken ct) =>
        AccountApiSupport.RunAsync(http, logger, async () =>
        {
            var input = await AccountApiSupport.ReadAsync<UpdateMyProfileRequest>(http.Request, ct);
            var version = AccountApiSupport.ExpectedVersion(http.Request);
            return Results.Ok(await service.UpdateProfileAsync(http.User, http, input, version, ct));
        });

    private static Task<IResult> SetupPasswordAsync(HttpContext http, AccountSelfServiceService service,
        ILogger<AccountNotice> logger, CancellationToken ct) =>
        AccountApiSupport.RunAsync(http, logger, async () =>
        {
            await service.RequestPasswordSetupAsync(http.User, ct);
            return Results.Accepted(value: AccountSelfServiceService.GenericForgotNotice);
        });
}
