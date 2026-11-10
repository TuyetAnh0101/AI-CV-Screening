using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Evaluation;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Prompts;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// A15 (phần 1): bộ CV đánh giá và nhãn HR.
///   GET    /api/evaluation-sets                              danh sách bộ + số nhãn
///   POST   /api/evaluation-sets                              tạo bộ mới
///   GET    /api/evaluation-sets/{id}                         một bộ + toàn bộ nhãn
///   GET    /api/evaluation-sets/{id}/applications?jobId=     hồ sơ có thể gán nhãn (kèm nhãn hiện tại)
///   PUT    /api/evaluation-sets/{id}/labels                  gán / sửa nhiều nhãn một lần (JSON)
///   POST   /api/evaluation-sets/{id}/labels/import           nhập nhãn từ file CSV (form: file, labeledBy)
///   GET    /api/evaluation-sets/{id}/labels/template         tải file CSV mẫu cho HR điền bằng Excel
///   DELETE /api/evaluation-sets/{id}/labels/{applicationId}  bỏ nhãn một hồ sơ
/// A16: nhãn đổi → tính lại accuracy trong cùng transaction và khóa bộ đánh giá.
/// </summary>
public static class EvaluationSetEndpoints
{
    public const int MaxLabelsPerRequest = 2000;
    public const long MaxCsvBytes = 1024 * 1024;   // 1 MB ~ hàng chục nghìn dòng

    public static WebApplication MapEvaluationSetEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() ||
            !app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled) return app;
        var group = app.MapGroup("/api/evaluation-sets").WithTags("EvaluationSets");
        group.MapGet("/", ListAsync).WithName("ListEvaluationSets").WithSummary("Các bộ CV đánh giá");
        group.MapPost("/", CreateAsync).WithName("CreateEvaluationSet").WithSummary("Tạo bộ CV đánh giá");
        group.MapGet("/{id:int}", GetAsync).WithName("GetEvaluationSet").WithSummary("Một bộ CV đánh giá và các nhãn HR");
        group.MapGet("/{id:int}/applications", CandidatesAsync).WithName("ListLabelCandidates")
            .WithSummary("Hồ sơ có thể gán nhãn trong bộ này");
        group.MapPut("/{id:int}/labels", SaveLabelsAsync).WithName("SaveHrLabels").WithSummary("Gán / sửa nhãn HR");
        group.MapPost("/{id:int}/labels/import", ImportCsvAsync).WithName("ImportHrLabels")
            .WithSummary("Nhập nhãn HR từ file CSV")
            .Accepts<IFormFile>("multipart/form-data")
            // Keep Kestrel's existing total request limit; cap multipart parts and the CSV file separately.
            .WithFormOptions(multipartBodyLengthLimit: MaxCsvBytes + 64 * 1024)
            .RequireRateLimiting(CvUploadRateLimiting.PolicyName)
            // Same boundary as CV upload: Development only, no authentication cookie yet.
            .DisableAntiforgery();
        group.MapGet("/{id:int}/labels/template", TemplateAsync).WithName("HrLabelTemplate").WithSummary("File CSV mẫu");
        group.MapDelete("/{id:int}/labels/{applicationId:int}", DeleteLabelAsync).WithName("DeleteHrLabel")
            .WithSummary("Bỏ nhãn một hồ sơ");
        return app;
    }

    // ---------- GET /api/evaluation-sets ----------
    private static async Task<Ok<List<EvaluationSetDto>>> ListAsync(SmartCvDbContext db, CancellationToken ct)
    {
        var list = await db.EvaluationSets.AsNoTracking()
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.EvaluationSetId)
            .Select(s => new EvaluationSetDto
            {
                EvaluationSetId = s.EvaluationSetId,
                Name = s.Name,
                Description = s.Description,
                LabelCount = s.HrLabels.Count(),
                PassCount = s.HrLabels.Count(h => h.IsPass)
            })
            .ToListAsync(ct);
        return TypedResults.Ok(list);
    }

    // ---------- POST /api/evaluation-sets ----------
    private static async Task<Results<Created<EvaluationSetDto>, ProblemHttpResult>> CreateAsync(
        CreateEvaluationSetRequest request, SmartCvDbContext db, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        if (name.Length == 0) return Problem(400, "Thiếu tên", "Hãy đặt tên bộ CV đánh giá.");
        if (name.Length > 150) return Problem(400, "Tên quá dài", "Tên bộ tối đa 150 ký tự.");
        if (description is { Length: > 500 }) return Problem(400, "Mô tả quá dài", "Mô tả tối đa 500 ký tự.");
        if (await db.EvaluationSets.AnyAsync(s => s.Name == name, ct))
            return Problem(409, "Trùng tên", $"Đã có bộ CV đánh giá tên \"{name}\".");

        var set = new EvaluationSet { Name = name, Description = description, CreatedAt = DateTime.UtcNow };
        db.EvaluationSets.Add(set);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return Problem(409, "Trùng tên", "Tên bộ CV đánh giá vừa được yêu cầu khác sử dụng.");
        }

        return TypedResults.Created($"/api/evaluation-sets/{set.EvaluationSetId}", new EvaluationSetDto
        {
            EvaluationSetId = set.EvaluationSetId, Name = set.Name, Description = set.Description
        });
    }

    // ---------- GET /api/evaluation-sets/{id} ----------
    private static async Task<Results<Ok<EvaluationSetDetailDto>, ProblemHttpResult>> GetAsync(
        int id, SmartCvDbContext db, CancellationToken ct)
    {
        var set = await db.EvaluationSets.AsNoTracking()
            .Where(s => s.EvaluationSetId == id)
            .Select(s => new { s.EvaluationSetId, s.Name, s.Description, s.CreatedAt, RunCount = s.EvaluationRuns.Count() })
            .FirstOrDefaultAsync(ct);
        if (set is null) return NotFoundSet(id);

        var labels = await db.HrLabels.AsNoTracking()
            .Where(h => h.EvaluationSetId == id)
            .OrderBy(h => h.Application.Job.Title).ThenBy(h => h.ApplicationId)
            .Select(h => new
            {
                h.ApplicationId,
                CandidateName = h.Application.Cv.Candidate != null ? h.Application.Cv.Candidate.FullName : null,
                FileName = h.Application.Cv.OriginalFileName,
                h.Application.JobId,
                JobTitle = h.Application.Job.Title,
                h.IsPass, h.Note, h.LabeledBy, h.LabeledAt
            })
            .ToListAsync(ct);

        return TypedResults.Ok(new EvaluationSetDetailDto
        {
            EvaluationSetId = set.EvaluationSetId,
            Name = set.Name,
            Description = set.Description,
            CreatedAt = Utc(set.CreatedAt),
            RunCount = set.RunCount,
            LabelCount = labels.Count,
            PassCount = labels.Count(l => l.IsPass),
            Labels = labels.Select(l => new HrLabelDto
            {
                ApplicationId = l.ApplicationId,
                CandidateName = l.CandidateName,
                FileName = l.FileName,
                JobId = l.JobId,
                JobTitle = l.JobTitle,
                IsPass = l.IsPass,
                Note = l.Note,
                LabeledBy = l.LabeledBy,
                LabeledAt = Utc(l.LabeledAt)
            }).ToList()
        });
    }

    // ---------- GET /api/evaluation-sets/{id}/applications ----------
    private static async Task<Results<Ok<List<LabelCandidateDto>>, ProblemHttpResult>> CandidatesAsync(
        int id, int? jobId, SmartCvDbContext db, CancellationToken ct)
    {
        if (!await db.EvaluationSets.AnyAsync(s => s.EvaluationSetId == id, ct)) return NotFoundSet(id);

        var q = db.Applications.AsNoTracking();
        if (jobId is { } j) q = q.Where(a => a.JobId == j);

        var list = await q
            .OrderBy(a => a.Job.Title).ThenBy(a => a.ApplicationId)
            .Select(a => new LabelCandidateDto
            {
                ApplicationId = a.ApplicationId,
                CandidateName = a.Cv.Candidate != null ? a.Cv.Candidate.FullName : null,
                FileName = a.Cv.OriginalFileName,
                JobId = a.JobId,
                JobTitle = a.Job.Title,
                ExtractionStatus = a.Cv.ExtractionStatus,
                CurrentLabel = a.HrLabels.Where(h => h.EvaluationSetId == id).Select(h => (bool?)h.IsPass).FirstOrDefault()
            })
            .ToListAsync(ct);
        return TypedResults.Ok(list);
    }

    // ---------- PUT /api/evaluation-sets/{id}/labels ----------
    private static async Task<Results<Ok<SaveHrLabelsResult>, ProblemHttpResult>> SaveLabelsAsync(
        int id, SaveHrLabelsRequest request, SmartCvDbContext db, CancellationToken ct) =>
        await SaveCoreAsync(db, id, request.LabeledBy, request.Labels ?? [], [], ct);

    // ---------- POST /api/evaluation-sets/{id}/labels/import ----------
    private static async Task<Results<Ok<SaveHrLabelsResult>, ProblemHttpResult>> ImportCsvAsync(
        int id, IFormFile? file, [FromForm] string? labeledBy, SmartCvDbContext db, CancellationToken ct)
    {
        if (!await db.EvaluationSets.AnyAsync(s => s.EvaluationSetId == id, ct)) return NotFoundSet(id);
        if (file is null || file.Length == 0) return Problem(400, "Chưa chọn file", "Gửi file CSV trong trường \"file\".");
        if (file.Length > MaxCsvBytes) return Problem(400, "File quá lớn", "File CSV tối đa 1 MB.");
        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return Problem(400, "Sai loại file", "Hãy lưu từ Excel bằng \"CSV UTF-8 (Comma delimited)\" (đuôi .csv).");

        string content;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream(), new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false);
            content = await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            return Problem(400, "Sai mã hóa CSV", "Hãy lưu file bằng UTF-8 hoặc UTF-8 BOM.");
        }

        var parsed = HrLabelCsvParser.Parse(content);
        if (!parsed.IsValid)
            return Problem(400, "File CSV có lỗi", string.Join(" | ", parsed.Errors.Take(20)));

        // Dòng chỉ có tên file → tìm mã hồ sơ. Một file nộp vào nhiều job thì không đoán, báo lỗi.
        var names = parsed.Rows.Where(r => r.ApplicationId is null)
            .Select(r => r.FileName!.ToLowerInvariant()).Distinct().ToList();
        var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        if (names.Count > 0)
        {
            var found = await db.Applications.AsNoTracking()
                .Where(a => names.Contains(a.Cv.OriginalFileName.ToLower()))
                .Select(a => new { a.ApplicationId, a.Cv.OriginalFileName })
                .ToListAsync(ct);
            foreach (var g in found.GroupBy(a => a.OriginalFileName, StringComparer.OrdinalIgnoreCase))
                byName[g.Key] = g.Select(x => x.ApplicationId).ToList();
        }

        var errors = new List<string>();
        var labels = new List<HrLabelInput>();
        var idsInFile = parsed.Rows.Where(r => r.ApplicationId != null).Select(r => r.ApplicationId!.Value).ToList();
        var knownIds = (await db.Applications.AsNoTracking().Where(a => idsInFile.Contains(a.ApplicationId))
            .Select(a => a.ApplicationId).ToListAsync(ct)).ToHashSet();
        var resolvedLines = new Dictionary<int, int>();
        foreach (var row in parsed.Rows)
        {
            var appId = row.ApplicationId;
            if (appId is null)
            {
                if (!byName.TryGetValue(row.FileName!, out var ids)) { errors.Add($"Dòng {row.LineNumber}: không có hồ sơ nào với file \"{row.FileName}\"."); continue; }
                if (ids.Count > 1) { errors.Add($"Dòng {row.LineNumber}: tên file \"{row.FileName}\" khớp {ids.Count} hồ sơ, hãy ghi ApplicationId."); continue; }
                appId = ids[0];
            }
            else if (!knownIds.Contains(appId.Value))
            {
                errors.Add($"Dòng {row.LineNumber}: hồ sơ {appId} không tồn tại.");
                continue;
            }
            if (!resolvedLines.TryAdd(appId.Value, row.LineNumber))
            {
                errors.Add($"Dòng {row.LineNumber}: hồ sơ {appId} đã có ở dòng {resolvedLines[appId.Value]}.");
                continue;
            }
            labels.Add(new HrLabelInput { ApplicationId = appId.Value, IsPass = row.IsPass, Note = row.Note });
        }
        if (errors.Count > 0) return Problem(400, "File CSV có lỗi", string.Join(" | ", errors.Take(20)));

        return await SaveCoreAsync(db, id, labeledBy, labels, [$"Đã đọc {parsed.Rows.Count} dòng CSV có kết quả."], ct);
    }

    // ---------- GET /api/evaluation-sets/{id}/labels/template ----------
    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> TemplateAsync(
        int id, int? jobId, SmartCvDbContext db, CancellationToken ct)
    {
        if (!await db.EvaluationSets.AnyAsync(s => s.EvaluationSetId == id, ct)) return NotFoundSet(id);

        var q = db.Applications.AsNoTracking();
        if (jobId is { } j) q = q.Where(a => a.JobId == j);

        var rows = await q
            .Where(a => a.Cv.ExtractionStatus != ExtractionStatuses.Failed)   // CV không đọc được: không đưa vào bộ đánh giá
            .OrderBy(a => a.Job.Title).ThenBy(a => a.ApplicationId)
            .Select(a => new
            {
                a.ApplicationId,
                a.Cv.OriginalFileName,
                CandidateName = a.Cv.Candidate != null ? a.Cv.Candidate.FullName : null,
                a.Job.Title,
                Label = a.HrLabels.Where(h => h.EvaluationSetId == id).Select(h => new { h.IsPass, h.Note }).FirstOrDefault()
            })
            .ToListAsync(ct);

        var csv = HrLabelCsvParser.BuildTemplate(rows.Select(r =>
            (r.ApplicationId, r.OriginalFileName, r.CandidateName, r.Title, r.Label?.IsPass, r.Label?.Note)));
        return TypedResults.File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"nhan-hr-bo-{id}.csv");
    }

    // ---------- DELETE /api/evaluation-sets/{id}/labels/{applicationId} ----------
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteLabelAsync(
        int id, int applicationId, SmartCvDbContext db, CancellationToken ct)
    {
        await using var transaction = await EvaluationSetWriteScope.BeginAsync(db, id, ct);
        var label = await db.HrLabels.FirstOrDefaultAsync(h => h.EvaluationSetId == id && h.ApplicationId == applicationId, ct);
        if (label is null) return Problem(404, "Không tìm thấy", $"Hồ sơ {applicationId} chưa có nhãn trong bộ #{id}.");

        db.HrLabels.Remove(label);
        await db.SaveChangesAsync(ct);
        await db.RecalculateSetAccuracyAsync(id, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------- lưu nhãn (dùng chung cho JSON và CSV) ----------
    private static async Task<Results<Ok<SaveHrLabelsResult>, ProblemHttpResult>> SaveCoreAsync(
        SmartCvDbContext db, int setId, string? labeledBy, List<HrLabelInput> input, List<string> warnings, CancellationToken ct)
    {
        await using var transaction = await EvaluationSetWriteScope.BeginAsync(db, setId, ct);
        if (!await db.EvaluationSets.AnyAsync(s => s.EvaluationSetId == setId, ct)) return NotFoundSet(setId);

        var by = (labeledBy ?? string.Empty).Trim();
        if (by.Length == 0) return Problem(400, "Thiếu người gán nhãn", "Ghi tên người gán nhãn, ví dụ \"HR - Trần Thu Hà\".");
        if (by.Length > 100) return Problem(400, "Tên quá dài", "Người gán nhãn tối đa 100 ký tự.");
        if (input.Count == 0) return Problem(400, "Chưa có nhãn", "Gửi ít nhất một nhãn.");
        if (input.Count > MaxLabelsPerRequest) return Problem(400, "Quá nhiều nhãn", $"Tối đa {MaxLabelsPerRequest} nhãn mỗi lần.");
        if (input.Any(l => l is null || l.ApplicationId <= 0))
            return Problem(400, "Nhãn không hợp lệ", "Mỗi nhãn phải có mã hồ sơ là số nguyên dương.");

        var dup = input.GroupBy(l => l.ApplicationId).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) return Problem(400, "Trùng hồ sơ", $"Hồ sơ {dup.Key} xuất hiện {dup.Count()} lần.");
        if (input.FirstOrDefault(l => l.Note is { Length: > 500 }) is { } longNote)
            return Problem(400, "Ghi chú quá dài", $"Ghi chú của hồ sơ {longNote.ApplicationId} quá 500 ký tự.");

        var ids = input.Select(l => l.ApplicationId).ToList();
        var apps = await db.Applications.AsNoTracking()
            .Where(a => ids.Contains(a.ApplicationId))
            .Select(a => new { a.ApplicationId, a.Cv.OriginalFileName, a.Cv.ExtractionStatus })
            .ToListAsync(ct);
        var missing = ids.Except(apps.Select(a => a.ApplicationId)).ToList();
        if (missing.Count > 0)
            return Problem(400, "Hồ sơ không tồn tại", $"Không có hồ sơ: {string.Join(", ", missing.Take(20))}.");

        foreach (var a in apps.Where(a => a.ExtractionStatus == ExtractionStatuses.Failed))
            warnings.Add($"Hồ sơ {a.ApplicationId} trích xuất thất bại; cần kiểm tra CV trước khi chạy đánh giá.");

        var existing = await db.HrLabels.Where(h => h.EvaluationSetId == setId && ids.Contains(h.ApplicationId))
            .ToDictionaryAsync(h => h.ApplicationId, ct);

        int added = 0, updated = 0, unchanged = 0;
        var now = DateTime.UtcNow;
        foreach (var l in input)
        {
            var note = string.IsNullOrWhiteSpace(l.Note) ? null : l.Note.Trim();
            if (existing.TryGetValue(l.ApplicationId, out var h))
            {
                if (h.IsPass == l.IsPass && h.Note == note) { unchanged++; continue; }
                h.IsPass = l.IsPass;
                h.Note = note;
                h.LabeledBy = by;
                h.LabeledAt = now;
                updated++;
            }
            else
            {
                db.HrLabels.Add(new HrLabel
                {
                    EvaluationSetId = setId, ApplicationId = l.ApplicationId, IsPass = l.IsPass,
                    Note = note, LabeledBy = by, LabeledAt = now
                });
                added++;
            }
        }
        await db.SaveChangesAsync(ct);
        // Re-read current HR labels after saving, while the A15 set lock is still held.
        var recalculated = added + updated > 0 ? await db.RecalculateSetAccuracyAsync(setId, ct) : [];
        if (added > 0 && await db.EvaluationRuns.AnyAsync(r => r.EvaluationSetId == setId, ct))
            warnings.Add($"Đã thêm {added} hồ sơ mới vào bộ: các lần chạy cũ giữ nguyên danh sách hồ sơ lúc tạo. Hãy chạy lại để đánh giá bộ hiện tại.");

        var total = await db.HrLabels.CountAsync(h => h.EvaluationSetId == setId, ct);
        var pass = await db.HrLabels.CountAsync(h => h.EvaluationSetId == setId && h.IsPass, ct);
        if (total > 0 && (pass == 0 || pass == total))
            warnings.Add("Bộ đánh giá cần có cả hồ sơ Đạt và Không đạt trước khi khởi chạy PromptTest.");

        if (transaction is not null) await transaction.CommitAsync(ct);

        return TypedResults.Ok(new SaveHrLabelsResult
        {
            Added = added, Updated = updated, Unchanged = unchanged,
            LabelCount = total, PassCount = pass,
            RecalculatedRunIds = recalculated, InvalidatedRunIds = [], Warnings = warnings
        });
    }

    private static ProblemHttpResult NotFoundSet(int id) => Problem(404, "Không tìm thấy", $"Không có bộ CV đánh giá #{id}.");

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: status);
}
