using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace SmartCv.Api.Auth;

public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Challenged)
        {
            if (context.Items.ContainsKey(AuthProblems.UnavailableItem)) { await AuthProblems.Unavailable().ExecuteAsync(context); return; }
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await AuthProblems.Problem(401, "authentication_required", "Chưa đăng nhập", "Bạn cần đăng nhập hoặc phiên đăng nhập đã hết hạn, hãy đăng nhập lại.").ExecuteAsync(context);
            return;
        }
        if (result.Forbidden)
        {
            await AuthProblems.Problem(403, "access_denied", "Không có quyền", "Tài khoản không có quyền thực hiện thao tác này.").ExecuteAsync(context);
            return;
        }
        await fallback.HandleAsync(next, context, policy, result);
    }
}
