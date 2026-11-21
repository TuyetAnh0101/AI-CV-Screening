using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using SmartCv.Core.Auth;

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
            if (context.User.HasClaim(SmartCvClaims.MustChangePassword, "true"))
            {
                await TypedResults.Problem(statusCode: 403, title: "Cần đổi mật khẩu", detail: "Bạn phải đổi mật khẩu trước khi sử dụng chức năng nghiệp vụ.",
                    extensions: new Dictionary<string, object?> { ["code"] = "password_change_required", ["mustChangePassword"] = true }).ExecuteAsync(context);
                return;
            }
            // A52: tài khoản hợp lệ nhưng phiên chưa có workspace đã xác nhận (token cấp trước A52, hoặc tài khoản chưa thuộc workspace nào)
            // là lỗi phiên (401, đăng nhập lại), khác với thiếu quyền (403).
            if (context.User.Identity?.IsAuthenticated == true && !HttpWorkspaceContext.HasValidatedWorkspace(context))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await AuthProblems.Problem(401, "authentication_required", "Chưa đăng nhập", "Phiên đăng nhập chưa gắn với không gian làm việc nào. Hãy đăng nhập lại.").ExecuteAsync(context);
                return;
            }
            await AuthProblems.Problem(403, "access_denied", "Không có quyền", "Tài khoản không có quyền thực hiện thao tác này.").ExecuteAsync(context);
            return;
        }
        await fallback.HandleAsync(next, context, policy, result);
    }
}
