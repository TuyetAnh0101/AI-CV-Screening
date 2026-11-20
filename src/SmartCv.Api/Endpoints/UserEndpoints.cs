using SmartCv.Api.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;

namespace SmartCv.Api.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(AuthPolicies.Admin);
        group.MapGet("/", ListAsync).WithName("ListUsers").Produces<List<UserDto>>();
        group.MapPost("/", CreateAsync).WithName("CreateUser").RequireRateLimiting(AuthSetup.AccountCredentialRateLimit).Produces<UserCredentialResponse>(201);
        group.MapPut("/{id:int}", UpdateAsync).WithName("UpdateUser").Produces<UserDto>();
        group.MapPost("/{id:int}/lock", (int id, HttpContext http, UserAccountService service, ILogger<User> logger, CancellationToken ct) => SetActiveAsync(id, false, http, service, logger, ct)).WithName("LockUser").Produces<UserDto>();
        group.MapPost("/{id:int}/unlock", (int id, HttpContext http, UserAccountService service, ILogger<User> logger, CancellationToken ct) => SetActiveAsync(id, true, http, service, logger, ct)).WithName("UnlockUser").Produces<UserDto>();
        group.MapPost("/{id:int}/reset-password", ResetAsync).WithName("ResetUserPassword").RequireRateLimiting(AuthSetup.AccountCredentialRateLimit).Produces<UserCredentialResponse>();
        return app;
    }
    private static Task<IResult> ListAsync(HttpContext http, UserAccountService service, ILogger<User> logger, CancellationToken ct) =>
        AccountApiSupport.RunAsync(http, logger, async () => Results.Ok(await service.ListAsync(http, ct)));
    private static Task<IResult> CreateAsync(HttpContext http, UserAccountService service, ILogger<User> logger, CancellationToken ct) =>
        AccountApiSupport.RunAsync(http, logger, async () =>
        {
            var created = await service.CreateAsync(await AccountApiSupport.ReadAsync<CreateUserRequest>(http.Request, ct), http.User, ct);
            Etag(http, created.User);
            return Results.Created($"/api/users/{created.User.UserId}", created);
        });
    private static Task<IResult> UpdateAsync(int id, HttpContext http, UserAccountService service, ILogger<User> logger, CancellationToken ct) =>
        AccountApiSupport.RunAsync(http, logger, async () =>
        {
            var request = await AccountApiSupport.ReadAsync<UpdateUserRequest>(http.Request, ct);
            var user = await service.UpdateAsync(id, request, http.User, AccountApiSupport.ExpectedVersion(http.Request, request.ExpectedVersion), ct);
            Etag(http, user); return Results.Ok(user);
        });
    private static Task<IResult> SetActiveAsync(int id, bool active, HttpContext http, UserAccountService service, ILogger<User> logger, CancellationToken ct) =>
        AccountApiSupport.RunAsync(http, logger, async () =>
        {
            var user = await service.SetActiveAsync(id, active, http.User, AccountApiSupport.ExpectedVersion(http.Request), ct);
            Etag(http, user); return Results.Ok(user);
        });
    private static Task<IResult> ResetAsync(int id, HttpContext http, UserAccountService service, ILogger<User> logger, CancellationToken ct) =>
        AccountApiSupport.RunAsync(http, logger, async () =>
        {
            var result = await service.ResetPasswordAsync(id, http.User, AccountApiSupport.ExpectedVersion(http.Request), ct);
            Etag(http, result.User); return Results.Ok(result);
        });
    private static void Etag(HttpContext http, UserDto user) => http.Response.Headers.ETag = '"' + user.Version + '"';
}