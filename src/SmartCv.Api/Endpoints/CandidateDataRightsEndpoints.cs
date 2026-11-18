using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Candidates;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Candidates;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Privacy;

namespace SmartCv.Api.Endpoints;

public static partial class CandidateEndpoints
{
    // ---------- GET /api/candidates/{id}/data-export (A43) ----------
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new ExportUtcConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // giữ nguyên chữ tiếng Việt cho ứng viên đọc được
    };

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> ExportAsync(
        int id, HttpContext http, SmartCvDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var values = http.Request.Query["handledBy"];
        var actor = string.IsNullOrWhiteSpace(values.FirstOrDefault()) ? "HR" : values[0]!.Trim();
        if (values.Count > 1 || values.Any(v => v?.Any(char.IsControl) == true) || actor.Length > CandidateErasePlan.MaxHandledByLength || actor.Any(char.IsControl))
            return Invalid(new() { ["handledBy"] = ["Người xử lý tối đa 100 ký tự, không có ký tự điều khiển; chỉ gửi một lần."] });
        await using var write = await CandidateWriteScope.BeginAsync(db, ct);
        var c = await db.Candidates.AsNoTracking().FirstOrDefaultAsync(x => x.CandidateId == id, ct);
        if (c is null) return NotFound(id);

        var now = clock.GetUtcNow().UtcDateTime;

        var cvs = await db.Cvs.AsNoTracking()
            .Where(v => v.CandidateId == id)
            .OrderBy(v => v.UploadedAt).ThenBy(v => v.CvId)
            .Select(v => new ExportCvDto
            {
                CvId = v.CvId,
                FileName = v.AnonymizedAt == null ? v.OriginalFileName : SmartCv.Core.Privacy.RetentionRules.AnonymizedFileName,
                ContentType = v.ContentType,
                FileSizeBytes = v.FileSizeBytes,
                UploadedAt = v.UploadedAt,
                SourceChannel = v.SourceChannel,
                ConsentConfirmedAt = v.ConsentConfirmedAt,
                RetainUntil = v.RetainUntil,
                AnonymizedAt = v.AnonymizedAt,
                ExtractedText = v.AnonymizedAt == null ? v.ExtractedText : null,
                Applications = v.Applications.OrderBy(a => a.CreatedAt).ThenBy(a => a.ApplicationId).Select(a => new ExportApplicationDto
                {
                    ApplicationId = a.ApplicationId,
                    JobTitle = a.Job.Title,
                    AppliedAt = a.CreatedAt,
                    Stage = a.Stage,
                    RejectReason = a.RejectReason,
                    Evaluations = a.Evaluations.OrderBy(e => e.CreatedAt).ThenBy(e => e.EvaluationId).Select(e => new ExportEvaluationDto
                    {
                        CreatedAt = e.CreatedAt,
                        RunType = e.Run.RunType,
                        KeywordScore = e.KeywordScore,
                        AiScore = e.AiScore,
                        FinalScore = e.FinalScore,
                        FitLabel = e.FitLabel,
                        YearsExperience = e.YearsExperience,
                        Reasoning = v.AnonymizedAt == null ? e.Reasoning : null,
                        Skills = e.Skills.Select(s => new ExportSkillDto { SkillCode = s.SkillCode, IsMatched = s.IsMatched, Evidence = v.AnonymizedAt == null ? s.Evidence : null }).ToList()
                    }).ToList(),
                    Events = a.Events.OrderBy(e => e.CreatedAt).ThenBy(e => e.EventId).Select(e => new ApplicationEventDto
                    {
                        EventId = e.EventId, ApplicationId = e.ApplicationId, FromStage = e.FromStage, ToStage = e.ToStage,
                        RejectReason = e.RejectReason, Note = e.Note, Actor = e.Actor, CreatedAt = e.CreatedAt
                    }).ToList(),
                    HrLabels = a.HrLabels.Select(l => new ExportHrLabelDto
                    {
                        EvaluationSetName = l.EvaluationSet.Name, IsPass = l.IsPass, Note = l.Note, LabeledBy = l.LabeledBy, LabeledAt = l.LabeledAt
                    }).ToList()
                }).ToList()
            })
            .AsSplitQuery()
            .ToListAsync(ct);

        var previous = await db.CandidateDataRequests.AsNoTracking()
            .Where(r => r.CandidateId == id).OrderBy(r => r.CreatedAt).ThenBy(r => r.RequestId).ToListAsync(ct);

        var export = new CandidateDataExportDto
        {
            ExportedAt = now,
            ExportedBy = actor,
            Notice = "Đây là toàn bộ dữ liệu SmartCv đang lưu về bạn. File CV gốc gửi kèm riêng nếu bạn yêu cầu. " +
                     "Điểm và nhận xét do hệ thống gợi ý, quyết định tuyển dụng do bộ phận nhân sự đưa ra.",
            Candidate = ToDto(c),
            Cvs = cvs,
            PreviousRequests = previous.Select(ToRequestDto).ToList()
        };

        // Ghi nhật ký TRƯỚC khi trả file: có bằng chứng đã cung cấp dữ liệu, lúc nào, ai làm
        var log = new CandidateDataRequest
        {
            CandidateId = id,
            RequestType = DataRequestTypes.Export,
            HandledBy = actor,
            ResultSummary = $"Xuất dữ liệu: {cvs.Count} CV, {cvs.Sum(v => v.Applications.Count)} hồ sơ ứng tuyển",
            CvIds = JoinIds(cvs.Select(v => v.CvId)),
            CreatedAt = now
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(export, ExportJson);
        db.CandidateDataRequests.Add(log);
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);

        http.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.File(bytes, "application/json", $"du-lieu-ung-vien-{id}-{now:yyyyMMdd}.json");
    }


    private static async Task<IResult> EraseAsync(int id, HttpContext http, SmartCvDbContext db,
        CandidateErasureService erasure, CancellationToken ct)
    {
        var (request, error) = await CandidateRequestReader.ReadAsync<EraseCandidateRequest>(http.Request, ct);
        if (error is not null) return error;
        if (request is null) return InvalidBody();
        var errors = CandidateErasePlan.Validate(request.Confirm, request.HandledBy, request.Note);
        if (errors.Count > 0) return Invalid(errors);
        CandidateErasureAttempt attempt;
        try { attempt = await erasure.EraseAsync(id, request, ct); }
        catch (CvPrivacyBusyException)
        {
            return TypedResults.Problem(statusCode: 503, title: "Dữ liệu đang được bảo vệ",
                detail: "Đợi thao tác hiện tại kết thúc và kiểm tra trạng thái trước khi xử lý tiếp.");
        }
        return attempt.Outcome switch
        {
            CandidateErasureOutcome.Done => TypedResults.Ok(attempt.Result),
            CandidateErasureOutcome.Missing => NotFound(id),
            CandidateErasureOutcome.AlreadyErased => TypedResults.Problem(statusCode: 410, title: "Đã xóa dữ liệu",
                detail: "Yêu cầu xóa đã được xử lý.",
                extensions: new Dictionary<string, object?> { ["requestId"] = attempt.RequestId, ["handledAt"] = attempt.HandledAt }),
            CandidateErasureOutcome.Blocked => TypedResults.Problem(statusCode: 409, title: "Chưa xóa được dữ liệu",
                detail: "CV đang được xử lý hoặc có file dùng chung ngoài ứng viên. Hãy kiểm tra trước khi xử lý tiếp."),
            _ => TypedResults.Problem(statusCode: 503, title: "Chưa hỗ trợ xử lý an toàn",
                detail: "Cần SQL Server và kho lưu trữ có cơ chế phục hồi. Chưa xóa dữ liệu.")
        };
    }

    private static async Task<IResult> ListRequestsAsync(HttpContext http, SmartCvDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var ids = http.Request.Query["candidateId"];
        int? candidateId = null;
        if (ids.Count > 1 || (ids.Count == 1 && !string.IsNullOrWhiteSpace(ids[0])
            && (!int.TryParse(ids[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var _) || int.Parse(ids[0]!, CultureInfo.InvariantCulture) < 1)))
            errors["candidateId"] = ["candidateId phải là số nguyên dương và chỉ gửi một lần."];
        else if (ids.Count == 1 && !string.IsNullOrWhiteSpace(ids[0])) candidateId = int.Parse(ids[0]!, CultureInfo.InvariantCulture);
        var types = http.Request.Query["type"];
        var type = string.IsNullOrWhiteSpace(types.FirstOrDefault()) ? null : types[0];
        if (types.Count > 1 || type is not (null or DataRequestTypes.Export or DataRequestTypes.Erase))
            errors["type"] = ["type phải là Export hoặc Erase và chỉ gửi một lần."];
        if (errors.Count > 0) return Invalid(errors);
        var query = db.CandidateDataRequests.AsNoTracking();
        if (candidateId is { } id) query = query.Where(r => r.CandidateId == id);
        if (type is not null) query = query.Where(r => r.RequestType == type);
        var rows = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RequestId).Take(200).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ToRequestDto).ToList());
    }

    private static DataRequestDto ToRequestDto(CandidateDataRequest row) => new()
    {
        RequestId = row.RequestId, CandidateId = row.CandidateId, RequestType = row.RequestType,
        RequestTypeName = DataRequestTypes.DisplayName(row.RequestType), HandledBy = row.HandledBy,
        Note = row.Note, ResultSummary = row.ResultSummary, CreatedAt = Utc(row.CreatedAt)
    };
    private static string JoinIds(IEnumerable<Guid> ids) => string.Join(",", ids);

    private sealed class ExportUtcConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDateTime();
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}

internal sealed class CandidateErasureEndpoint;
