using Microsoft.AspNetCore.Http.HttpResults;
using SmartCv.Core.Contracts;

namespace SmartCv.Api.Auditing;

public sealed class AuditEndpointFilter(AuditTrail audit) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var action = AuditCatalog.For(http);
        if (action is null) return await next(context);
        object? result;
        try { result = await next(context); }
        catch
        {
            await audit.RecordAsync(action, RouteResource(http, action), "Failed", "operation_failed", CancellationToken.None);
            throw;
        }
        if (audit.IsRecorded) return result;
        var leaf = result;
        while (leaf is INestedHttpResult nested) leaf = nested.Result;
        var status = (leaf as IStatusCodeHttpResult)?.StatusCode ?? http.Response.StatusCode;
        var value = (leaf as IValueHttpResult)?.Value;
        var outcome = status >= 500 ? "Failed" : status >= 400 ? "Denied" : "Succeeded";
        var failure = status >= 400 ? "http_" + status : null;
        if (action.Code == "auth.login" && status >= 400)
            failure = status switch { 401 => "invalid_credentials", 403 => "account_locked", 429 => "rate_limited", >= 500 => "auth_unavailable", _ => "invalid_request" };
        switch (value)
        {
            case CvUploadResponse upload:
                outcome = upload.SuccessCount == 0 ? "Denied" : upload.FailedCount > 0 ? "Partial" : "Succeeded";
                failure = upload.SuccessCount == 0 ? "upload_rejected" : upload.FailedCount > 0 ? "partial_result" : null; break;
            case AttachCandidateResponse { Outcome: AttachOutcomes.NeedsDecision }: outcome = "NoChange"; failure = "decision_required"; break;
            case StageChangeResultDto { Outcome: "Unchanged" }: outcome = "NoChange"; break;
            case BulkStageChangeResponse bulk when bulk.NotFound > 0: outcome = bulk.Changed > 0 ? "Partial" : "NoChange"; failure = "partial_result"; break;
            case BulkStageChangeResponse { Changed: 0 }: outcome = "NoChange"; break;
            case CreateApplicationsResponse applications when applications.FailedCount > 0:
                outcome = applications.CreatedCount + applications.ExistingCount > 0 ? "Partial" : "Denied"; failure = "partial_result"; break;
            case CreateApplicationsResponse { CreatedCount: 0 }: outcome = "NoChange"; break;
            case AddEvaluationSetMembersResult members when members.Skipped.Any(s => s.Reason != SkippedMemberReasons.AlreadyMember):
                outcome = members.Added > 0 ? "Partial" : "Denied"; failure = "partial_result"; break;
            case AddEvaluationSetMembersResult { Added: 0 }: outcome = "NoChange"; break;
            case AnonymizeResultDto { AlreadyAnonymized: true }: outcome = "NoChange"; break;
            case RetentionRunResultDto retention when retention.FailedCount > 0:
                outcome = retention.AnonymizedCount > 0 ? "Partial" : "Failed"; failure = "partial_result"; break;
            case RetentionRunResultDto { DryRun: true }: outcome = "NoChange"; break;
        }
        if (action.Required && status < 400) outcome = "AccessGranted";
        var resource = RouteResource(http, action) ?? (value switch
        {
            JobDto job => job.JobId.ToString(), CandidateDto candidate => candidate.CandidateId.ToString(),
            PromptVersionDto prompt => prompt.PromptVersionId.ToString(), EvaluationSetDto set => set.EvaluationSetId.ToString(),
            RunProgressDto run => run.RunId.ToString(), _ => null
        });
        var recorded = await audit.RecordAsync(action, resource, outcome, failure, http.RequestAborted);
        if (!recorded && action.Required && status < 400)
        {
            if (leaf is FileStreamHttpResult file) await file.FileStream.DisposeAsync();
            return Results.Problem(statusCode: 503, title: "Chưa thể cấp quyền đọc file", detail: "Dịch vụ ghi nhận truy cập tạm không khả dụng. Vui lòng thử lại.",
                extensions: new Dictionary<string, object?> { ["code"] = "audit_unavailable" });
        }
        if (!recorded && !http.Response.HasStarted) http.Response.Headers["X-SCApp-Audit"] = "unavailable";
        return result;
    }
    public static string? RouteResource(HttpContext http, AuditAction action) => action.RouteKey is { } key
        ? AuditTrail.SafeResource(http.Request.RouteValues.GetValueOrDefault(key)?.ToString()) : null;
}
