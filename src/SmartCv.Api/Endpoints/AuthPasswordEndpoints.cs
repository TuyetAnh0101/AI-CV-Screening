using SmartCv.Api.Auth;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Endpoints;

internal static class AuthPasswordEndpoints
{
    internal static void Map(RouteGroupBuilder group) => group.MapPost("/change-password", ChangeAsync)
        .WithName("ChangePassword").RequireAuthorization(AuthPolicies.SignedIn)
        .RequireRateLimiting(AuthSetup.AccountCredentialRateLimit).Produces<ChangePasswordResponse>();

    private static Task<IResult> ChangeAsync(HttpContext http, UserAccountService service, JwtTokenService tokens,
        ILogger<ChangePasswordRequest> logger, CancellationToken ct) => AccountApiSupport.RunAsync(http, logger, async () =>
    {
        var request = await AccountApiSupport.ReadAsync<ChangePasswordRequest>(http.Request, ct);
        var user = await service.ChangePasswordAsync(request, http.User, AccountApiSupport.ExpectedVersion(http.Request), ct);
        var (token, expires) = tokens.Create(user);
        return Results.Ok(new ChangePasswordResponse { Token = token, ExpiresAt = expires, User = AuthMapping.ToMe(user) });
    });
}