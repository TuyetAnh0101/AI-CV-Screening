using Microsoft.Extensions.Options;
using SmartCv.Api.Endpoints;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Privacy;

namespace SmartCv.Api.Middleware;
/// <summary>Keep an erasure from overtaking an in-flight personal-data response. No response body cache.</summary>
public sealed class CvPrivacyReadMiddleware(RequestDelegate next)
{
    // A41: assembled-preview trả nội dung CV (đã che) nên cũng giữ khóa đọc, để ẩn danh hóa không chen ngang lúc đang trả.
    private static readonly string[] PersonalRoutes = ["/api/cvs", "/api/runs", "/api/evaluations", "/api/applications", "/api/evaluation-sets", "/api/prompt-tests", "/api/prompts/compare", "/api/prompts/assembled-preview"];
    public async Task InvokeAsync(HttpContext http, IServiceScopeFactory scopes, IHostEnvironment environment, IOptions<CvUploadEndpointOptions> endpoint)
    {
        var path = http.Request.Path;
        // Writes such as HR stages, run creation and re-extraction take the same shared lease first,
        // so they cannot pass eligibility checks and then resume behind an erasure commit.
        var personalResponse = PersonalRoutes.Any(route => path.StartsWithSegments(route)) && path.Value?.EndsWith("/anonymize", StringComparison.OrdinalIgnoreCase) != true && !path.StartsWithSegments("/api/cvs/upload");
        if (!personalResponse || !environment.IsDevelopment() || !endpoint.Value.Enabled)
        {
            await next(http);
            return;
        }

        using var scope = scopes.CreateScope();
        CvPrivacyScope? lease;
        try
        {
            lease = await CvPrivacyScope.BeginAsync(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>(), false, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Results.Problem(statusCode: 503, title: "Chưa đọc được dữ liệu CV", detail: "Dữ liệu đang được bảo vệ hoặc kết nối chưa sẵn sàng. Hãy thử tải lại sau.").ExecuteAsync(http);
            return;
        }

        await using (lease)
        {
            http.Response.Headers.CacheControl = "no-store";
            await next(http); // includes serialization/file streaming, not merely DTO construction
        }
    }
}
