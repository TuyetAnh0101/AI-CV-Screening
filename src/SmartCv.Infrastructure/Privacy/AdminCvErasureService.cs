using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Infrastructure.Privacy;

/// <summary>
/// Đường xử lý vận hành có chủ đích cho Admin: chỉ tìm WorkspaceId của đúng
/// một CV theo ID, rồi thực hiện quy trình ẩn danh hóa hiện có trong workspace đó.
/// Không cho Admin xem CV, không nhận workspaceId từ HTTP và không mở
/// system scope trong endpoint. Việc cấp quyền SystemAdmin bắt buộc tại route.
/// </summary>
public sealed class AdminCvErasureService(SmartCvDbContext db, CvAnonymizer anonymizer)
{
    public async Task<AnonymizeAttempt> AnonymizeAsync(
        Guid cvId, string actor, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var workspaceId = await WorkerWorkspaces.OfCvAsync(db, cvId, cancellationToken);
        if (workspaceId is not > 0)
            return new AnonymizeAttempt(AnonymizeOutcome.NotFound, null, null);

        using var workspace = WorkspaceScope.Begin(workspaceId.Value);
        return await anonymizer.AnonymizeAsync(
            cvId, actor, automatic: false, nowUtc: nowUtc, ct: cancellationToken);
    }
}
