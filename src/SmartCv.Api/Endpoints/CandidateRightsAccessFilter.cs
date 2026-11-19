namespace SmartCv.Api.Endpoints;

/// <summary>Defense in depth for candidate rights; always fails closed without an authenticated operator.</summary>
internal sealed class CandidateRightsAccessFilter : IEndpointFilter
{
    public CandidateRightsAccessFilter() { }
    internal static Task<int> StatusAsync(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true) return Task.FromResult(401);
        return Task.FromResult(http.User.IsInRole("HR") || http.User.IsInRole("Admin") ? 0 : 403);
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var status = await StatusAsync(context.HttpContext);
        return status == 0 ? await next(context)
            : Results.Problem(statusCode: status, title: status == 401 ? "Cần đăng nhập để quản lý dữ liệu CV" : "Cần quyền HR hoặc Admin");
    }
}
