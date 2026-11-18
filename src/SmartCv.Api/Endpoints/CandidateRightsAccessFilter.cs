using Microsoft.AspNetCore.Authentication;

namespace SmartCv.Api.Endpoints;

/// <summary>Preserves A39's HR/Admin boundary when authentication schemes are configured.</summary>
internal sealed class CandidateRightsAccessFilter : IEndpointFilter
{
    public CandidateRightsAccessFilter() { }
    internal static async Task<int> StatusAsync(HttpContext http)
    {
        var schemes = http.RequestServices.GetService<IAuthenticationSchemeProvider>();
        if (schemes is null || !(await schemes.GetAllSchemesAsync()).Any()) return 0;
        if (http.User.Identity?.IsAuthenticated != true) return 401;
        return http.User.IsInRole("HR") || http.User.IsInRole("Admin") ? 0 : 403;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var status = await StatusAsync(context.HttpContext);
        return status == 0 ? await next(context)
            : Results.Problem(statusCode: status, title: status == 401 ? "Cần đăng nhập để quản lý dữ liệu CV" : "Cần quyền HR hoặc Admin");
    }
}
