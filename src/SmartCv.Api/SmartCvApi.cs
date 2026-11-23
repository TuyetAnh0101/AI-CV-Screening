using SmartCv.Api.Auth;
using SmartCv.Api.Endpoints;
using SmartCv.Api.Ops;

namespace SmartCv.Api;

/// <summary>One authorization boundary for all business endpoints; the fallback policy covers future routes.</summary>
public static class SmartCvApi
{
    public static IEndpointRouteBuilder MapSmartCvApi(this IEndpointRouteBuilder app)
    {
        app.MapReadinessEndpoint();
        app.MapAuthEndpoints();
        app.MapControllers().RequireAuthorization(AuthPolicies.SystemAdmin);
        var api = app.MapGroup("").RequireAuthorization(AuthPolicies.BusinessOperator);
        api.AddEndpointFilter<SmartCv.Api.Auditing.AuditEndpointFilter>();
        // Administrative routes MUST NOT inherit BusinessOperator from api.
        // Keep the same audit filter for the separated operational routes.
        var operational = app.MapGroup("");
        operational.AddEndpointFilter<SmartCv.Api.Auditing.AuditEndpointFilter>();
        api.MapEvaluationEndpoints();
        api.MapRunEndpoints();
        operational.MapExportEndpoints();
        api.MapCvEndpoints();
        api.MapCandidateEndpoints();
        operational.MapRetentionEndpoints();
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
        operational.MapUserEndpoints();
        operational.MapAuditEndpoints();
        operational.MapAdminEmailEndpoints();
        api.MapWorkspaceEndpoints();
        
        return app;
    }
}
