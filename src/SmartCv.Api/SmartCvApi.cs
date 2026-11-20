using SmartCv.Api.Endpoints;

namespace SmartCv.Api;

/// <summary>One authorization boundary for all business endpoints; the fallback policy covers future routes.</summary>
public static class SmartCvApi
{
    public static IEndpointRouteBuilder MapSmartCvApi(this IEndpointRouteBuilder app)
    {
        app.MapAuthEndpoints();
        app.MapControllers().RequireAuthorization();
        var api = app.MapGroup("").RequireAuthorization();
        api.AddEndpointFilter<SmartCv.Api.Auditing.AuditEndpointFilter>();
        api.MapEvaluationEndpoints();
        api.MapRunEndpoints();
        api.MapCvEndpoints();
        api.MapCandidateEndpoints();
        api.MapRetentionEndpoints();
        api.MapApplicationEndpoints();
        api.MapApplicationStageEndpoints();
        api.MapManualFlagEndpoints();
        api.MapDashboardEndpoints();
        api.MapScoringPreviewEndpoints();
        api.MapAiPreviewEndpoints();
        api.MapJobEndpoints();
        api.MapJobSkillEndpoints();
        api.MapPromptEndpoints();
        api.MapEvaluationSetEndpoints();
        api.MapPromptTestEndpoints();
        api.MapUserEndpoints();
        api.MapAuditEndpoints();
        return app;
    }
}
