using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Evaluation;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Prompts;
using System.Security.Claims;
using SmartCv.Api.Auth;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// SCApp v2: thành viên bộ CV đánh giá, tách khỏi nhãn HR.
///   POST   /api/evaluation-sets/{id}/members                  thêm nhiều hồ sơ đã có (không upload lại, không thêm trùng)
///   DELETE /api/evaluation-sets/{id}/members/{applicationId}  bỏ hồ sơ khỏi bộ (bỏ luôn nhãn HR của hồ sơ trong bộ này)
/// Mọi thay đổi chạy dưới khóa bộ của A15 và tăng EvaluationSets.Revision.
/// Kết quả chấm đã lưu (Evaluations) KHÔNG bị sửa; tỷ lệ đúng của lượt cũ tính lại theo nhãn hiện tại như A16.
/// </summary>
public static partial class EvaluationSetEndpoints
{
    private static void MapMemberEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/{id:int}/members", AddMembersAsync).WithName("AddEvaluationSetMembers")
            .WithSummary("Thêm hồ sơ đã quét vào bộ CV đánh giá");
        group.MapDelete("/{id:int}/members/{applicationId:int}", RemoveMemberAsync).WithName("RemoveEvaluationSetMember")
            .WithSummary("Bỏ một hồ sơ khỏi bộ CV đánh giá");
    }

    // ---------- POST /api/evaluation-sets/{id}/members ----------
    private static async Task<Results<Ok<AddEvaluationSetMembersResult>, ProblemHttpResult>> AddMembersAsync(
        int id, AddEvaluationSetMembersRequest request, ClaimsPrincipal user, SmartCvDbContext db, CancellationToken ct)
    {
        var actorError = EvaluationSetRules.ValidateActor(user.ActorName(), out var by);
        if (actorError is not null) return Problem(400, "Thiếu người thao tác", actorError);
        var ids = request.ApplicationIds ?? [];
        if (ids.Count == 0) return Problem(400, "Chưa chọn hồ sơ", "Chọn ít nhất một hồ sơ để thêm vào bộ.");
        if (ids.Count > EvaluationSetRules.MaxMembersPerRequest)
            return Problem(400, "Quá nhiều hồ sơ", $"Mỗi lần thêm tối đa {EvaluationSetRules.MaxMembersPerRequest} hồ sơ.");
        if (ids.Any(a => a <= 0)) return Problem(400, "Hồ sơ không hợp lệ", "Mã hồ sơ phải là số nguyên dương.");
        var distinct = ids.Distinct().ToList();

        try
        {
            await using var transaction = await EvaluationSetWriteScope.BeginAsync(db, id, ct);
            var set = await db.EvaluationSets.FirstOrDefaultAsync(s => s.EvaluationSetId == id, ct);
            if (set is null) return NotFoundSet(id);

            var apps = await db.Applications.AsNoTracking().Where(a => distinct.Contains(a.ApplicationId))
                .Select(a => new
                {
                    a.ApplicationId, Anonymized = a.Cv.AnonymizedAt != null, a.Cv.ExtractionStatus,
                    HasText = a.Cv.ExtractedText != null && a.Cv.ExtractedText.Trim() != ""
                }).ToListAsync(ct);
            var existing = (await MembersOf(db, id).Where(a => distinct.Contains(a.ApplicationId))
                .Select(a => a.ApplicationId).ToListAsync(ct)).ToHashSet();

            var skipped = new List<SkippedMemberDto>();
            var added = new List<int>();
            var warnings = new List<string>();
            var now = DateTime.UtcNow;
            var found = apps.ToDictionary(a => a.ApplicationId);
            foreach (var appId in distinct)
            {
                if (!found.TryGetValue(appId, out var app))
                { skipped.Add(new() { ApplicationId = appId, Reason = SkippedMemberReasons.NotFound, Message = $"Hồ sơ #{appId} không tồn tại." }); continue; }
                if (existing.Contains(appId))
                { skipped.Add(new() { ApplicationId = appId, Reason = SkippedMemberReasons.AlreadyMember, Message = $"Hồ sơ #{appId} đã có trong bộ." }); continue; }
                // Hồ sơ đã ẩn danh không còn nội dung CV và không được dùng lại cho mục đích đánh giá.
                if (app.Anonymized)
                { skipped.Add(new() { ApplicationId = appId, Reason = SkippedMemberReasons.Anonymized, Message = $"Hồ sơ #{appId} đã ẩn danh, không thể đưa vào bộ." }); continue; }
                db.EvaluationSetMembers.Add(new EvaluationSetMember { EvaluationSetId = id, ApplicationId = appId, AddedBy = by, AddedAt = now });
                added.Add(appId);
            }
            var notReady = apps.Count(a => added.Contains(a.ApplicationId)
                && EvaluationSetRules.NotReadyReason(a.ExtractionStatus, a.HasText, a.Anonymized) is not null);
            if (notReady > 0)
                warnings.Add($"{notReady} hồ sơ vừa thêm chưa sẵn sàng (CV chưa trích xuất xong, lỗi hoặc không có chữ). Có thể gán nhãn sau khi CV đọc xong.");
            if (ids.Count != distinct.Count) warnings.Add("Danh sách gửi lên có mã hồ sơ lặp lại; mỗi hồ sơ chỉ được thêm một lần.");

            if (added.Count > 0)
            {
                BumpRevision(set, now);
                if (await db.EvaluationRuns.AnyAsync(r => r.EvaluationSetId == id, ct))
                    warnings.Add("Bộ đã có lượt chạy: các lượt cũ giữ nguyên danh sách hồ sơ lúc chạy. Hãy chạy lại để đánh giá bộ hiện tại.");
            }
            await db.SaveChangesAsync(ct);
            var count = await MembersOf(db, id).CountAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return TypedResults.Ok(new AddEvaluationSetMembersResult
            {
                Added = added.Count, AddedApplicationIds = added, Skipped = skipped,
                MemberCount = count, Revision = set.Revision, Warnings = warnings
            });
        }
        catch (Exception ex) when (ex.GetBaseException() is SqlException { Number: 1205 or 2601 or 2627 })
        {
            return Problem(409, "Xung đột cập nhật bộ CV", "Bộ CV vừa được một thao tác khác thay đổi. Hãy tải lại rồi thử lại.");
        }
    }

    // ---------- DELETE /api/evaluation-sets/{id}/members/{applicationId} ----------
    private static async Task<Results<Ok<RemoveEvaluationSetMemberResult>, ProblemHttpResult>> RemoveMemberAsync(
        int id, int applicationId, SmartCvDbContext db, CancellationToken ct)
    {
        await using var transaction = await EvaluationSetWriteScope.BeginAsync(db, id, ct);
        var set = await db.EvaluationSets.FirstOrDefaultAsync(s => s.EvaluationSetId == id, ct);
        if (set is null) return NotFoundSet(id);
        var member = await db.EvaluationSetMembers.FirstOrDefaultAsync(m => m.EvaluationSetId == id && m.ApplicationId == applicationId, ct);
        var label = await db.HrLabels.FirstOrDefaultAsync(h => h.EvaluationSetId == id && h.ApplicationId == applicationId, ct);
        if (member is null && label is null)
            return Problem(404, "Không tìm thấy", $"Hồ sơ {applicationId} không thuộc bộ #{id}.");

        if (member is not null) db.EvaluationSetMembers.Remove(member);
        if (label is not null) db.HrLabels.Remove(label);
        var now = DateTime.UtcNow;
        BumpRevision(set, now);
        await db.SaveChangesAsync(ct);
        var recalculated = label is not null ? await db.RecalculateSetAccuracyAsync(id, ct) : [];
        var warnings = new List<string>();
        if (await db.EvaluationRuns.AnyAsync(r => r.EvaluationSetId == id, ct))
            warnings.Add("Kết quả chấm của các lượt cũ được giữ nguyên; tỷ lệ đúng của chúng được tính lại theo nhãn HR hiện tại (hồ sơ đã bỏ không còn được tính).");
        var count = await MembersOf(db, id).CountAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return TypedResults.Ok(new RemoveEvaluationSetMemberResult
        {
            ApplicationId = applicationId, RemovedLabel = label is not null, MemberCount = count,
            Revision = set.Revision, RecalculatedRunIds = recalculated, Warnings = warnings
        });
    }

    /// <summary>Người gọi giữ khóa bộ (EvaluationSetWriteScope) và đã tải <paramref name="set"/> có theo dõi.</summary>
    private static void BumpRevision(EvaluationSet set, DateTime now)
    {
        set.Revision++;
        set.UpdatedAt = now;
    }

    private static async Task<List<EvaluationSetMemberDto>> LoadMembersAsync(SmartCvDbContext db, int id, CancellationToken ct)
    {
        // Thành viên = dòng EvaluationSetMembers HOẶC hồ sơ đã có nhãn trong bộ (dữ liệu nhập thẳng HrLabels, ví dụ seed_demo.sql).
        var rows = await MembersOf(db, id)
            .OrderBy(a => a.Job.Title).ThenBy(a => a.ApplicationId)
            .Select(a => new
            {
                a.ApplicationId, a.CvId, a.JobId, JobTitle = a.Job.Title, a.Stage,
                Anonymized = a.Cv.AnonymizedAt != null,
                CandidateName = a.Cv.AnonymizedAt == null && a.Cv.Candidate != null ? a.Cv.Candidate.FullName : null,
                FileName = a.Cv.AnonymizedAt == null ? a.Cv.OriginalFileName : SmartCv.Core.Privacy.RetentionRules.AnonymizedFileName,
                a.Cv.ExtractionStatus,
                HasText = a.Cv.ExtractedText != null && a.Cv.ExtractedText.Trim() != "",
                Member = a.EvaluationSetMemberships.Where(m => m.EvaluationSetId == id).Select(m => new { m.AddedBy, m.AddedAt }).FirstOrDefault(),
                Label = a.HrLabels.Where(h => h.EvaluationSetId == id)
                    .Select(h => new { h.IsPass, h.Note, h.LabeledBy, h.LabeledAt }).FirstOrDefault()
            }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var reason = EvaluationSetRules.NotReadyReason(r.ExtractionStatus, r.HasText, r.Anonymized);
            return new EvaluationSetMemberDto
            {
                ApplicationId = r.ApplicationId, CvId = r.CvId, CandidateName = r.CandidateName, FileName = r.FileName,
                JobId = r.JobId, JobTitle = r.JobTitle, Stage = r.Stage, ExtractionStatus = r.ExtractionStatus,
                HasExtractedText = r.HasText, IsAnonymized = r.Anonymized, IsReady = reason is null, NotReadyReason = reason,
                IsPass = r.Label?.IsPass,
                // Ghi chú HR có thể chứa thông tin cá nhân: không trả về cho hồ sơ đã ẩn danh.
                Note = r.Anonymized ? null : r.Label?.Note,
                LabeledBy = r.Label?.LabeledBy, LabeledAt = r.Label is null ? null : Utc(r.Label.LabeledAt),
                AddedBy = r.Member?.AddedBy ?? r.Label?.LabeledBy ?? string.Empty,
                AddedAt = Utc(r.Member?.AddedAt ?? r.Label?.LabeledAt ?? default)
            };
        }).ToList();
    }

    /// <summary>Hồ sơ thuộc bộ: có dòng thành viên hoặc có nhãn HR trong bộ (tương thích dữ liệu trước v2).</summary>
    internal static IQueryable<Application> MembersOf(SmartCvDbContext db, int id) => db.Applications.AsNoTracking()
        .Where(a => a.EvaluationSetMemberships.Any(m => m.EvaluationSetId == id) || a.HrLabels.Any(h => h.EvaluationSetId == id));

    private static EvaluationSetReadinessDto ReadinessOf(IEnumerable<EvaluationSetMemberDto> members) =>
        EvaluationSetRules.Readiness(members.Select(m => new EvaluationSetRules.MemberState(m.ApplicationId, m.IsPass, m.IsReady)).ToList());
}
