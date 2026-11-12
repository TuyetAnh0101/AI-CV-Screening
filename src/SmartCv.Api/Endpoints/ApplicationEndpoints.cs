using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// API hồ sơ ứng tuyển (bảng Applications: một CV ứng tuyển một job) – A8:
///   POST   /api/applications                   gắn nhiều CV vào một job
///   GET    /api/applications?jobId=1 | ?cvId=   danh sách hồ sơ (bắt buộc lọc theo job hoặc CV)
///   DELETE /api/applications/{id}              bỏ gắn (chỉ khi hồ sơ chưa được chấm, chưa có nhãn HR)
/// Các API này trả tên file / tên ứng viên (dữ liệu cá nhân) nên dùng chung chốt chặn với API CV:
/// chỉ có ở Development khi CvUpload:Endpoint:Enabled = true, cho đến khi có xác thực (M6).
/// </summary>
public static class ApplicationEndpoints
{
    public const int MaxCvsPerRequest = 100;

    public static WebApplication MapApplicationEndpoints(this WebApplication app)
    {
        var endpointOptions = app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value;

        // Môi trường khác Development mà bật Enabled thì MapCvEndpoints đã dừng ứng dụng lúc khởi động.
        if (!endpointOptions.Enabled || !app.Environment.IsDevelopment())
        {
            app.Logger.LogInformation("API hồ sơ ứng tuyển (/api/applications) đang TẮT: chỉ bật ở Development khi chưa có xác thực (M6).");
            return app;
        }

        // M6: thêm .RequireAuthorization(...) cho cả nhóm.
        var group = app.MapGroup("/api/applications").WithTags("Applications");

        group.MapPost("/", CreateAsync)
            .WithName("CreateApplications")
            .WithSummary("Gắn một hoặc nhiều CV vào một job")
            .RequireRateLimiting(CvUploadRateLimiting.PolicyName)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            // Không có cookie/phiên đăng nhập nên không có nguy cơ CSRF (giống API tải CV).
            .DisableAntiforgery();

        group.MapGet("/", ListAsync)
            .WithName("ListApplications")
            .WithSummary("Danh sách hồ sơ, lọc theo jobId hoặc cvId")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapDelete("/{id:int}", DeleteAsync)
            .WithName("DeleteApplication")
            .WithSummary("Bỏ gắn CV khỏi job (chưa chấm, chưa có nhãn HR)")
            .RequireRateLimiting(CvUploadRateLimiting.PolicyName)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .DisableAntiforgery();

        return app;
    }

    // ---------- POST /api/applications ----------
    private static async Task<Results<Ok<CreateApplicationsResponse>, ProblemHttpResult>> CreateAsync(
        CreateApplicationsRequest request, SmartCvDbContext db, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("SmartCv.Api.Applications");

        // 1. Kiểm tra dữ liệu gửi lên: bỏ mã rỗng, bỏ trùng (giữ thứ tự gửi lên)
        var cvIds = (request.CvIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (cvIds.Count == 0)
            return Problem(400, "Chưa chọn CV", "Hãy gửi ít nhất một mã CV trong \"cvIds\".");
        if (cvIds.Count > MaxCvsPerRequest)
            return Problem(400, "Quá nhiều CV", $"Mỗi lần chỉ được gắn tối đa {MaxCvsPerRequest} CV.");

        // 2. Job phải tồn tại và đang tuyển
        var job = await db.Jobs.AsNoTracking()
            .Where(j => j.JobId == request.JobId)
            .Select(j => new { j.JobId, j.Title, j.IsActive })
            .FirstOrDefaultAsync(ct);
        if (job is null)
            return Problem(404, "Không tìm thấy vị trí", $"Không có job với mã {request.JobId}.");
        if (!job.IsActive)
            return Problem(400, "Vị trí đã ngừng tuyển", $"\"{job.Title}\" không còn nhận hồ sơ.");

        // 3. Thông tin các CV (1 câu truy vấn cho cả danh sách)
        var cvs = await db.Cvs.AsNoTracking()
            .Where(c => cvIds.Contains(c.CvId))
            .Select(c => new { c.CvId, c.OriginalFileName, c.ExtractionStatus })
            .ToDictionaryAsync(c => c.CvId, ct);

        // 4. Tạo hồ sơ cho CV chưa gắn. Nếu người khác vừa gắn cùng CV vào cùng job (UQ_Applications_Cv_Job),
        //    đọc lại các hồ sơ đã có rồi thử thêm một lần: CV đó thành AlreadyExists thay vì lỗi cả request.
        Dictionary<Guid, int> existing = [];
        Dictionary<Guid, Application> newApps = [];
        for (var attempt = 1; ; attempt++)
        {
            existing = await db.Applications.AsNoTracking()
                .Where(a => a.JobId == job.JobId && cvIds.Contains(a.CvId))
                .ToDictionaryAsync(a => a.CvId, a => a.ApplicationId, ct);

            newApps = [];
            var createdAt = DateTime.UtcNow;
            foreach (var cvId in cvIds.Where(id => cvs.ContainsKey(id) && !existing.ContainsKey(id)))
            {
                var application = new Application { CvId = cvId, JobId = job.JobId, CreatedAt = createdAt };
                newApps[cvId] = application;
                db.Applications.Add(application);
            }

            try
            {
                await db.SaveChangesAsync(ct);   // sau dòng này, ApplicationId (IDENTITY) của hồ sơ mới đã có giá trị
                break;
            }
            catch (DbUpdateException ex) when (IsSql(ex, 2601, 2627) && attempt < 2)
            {
                logger.LogWarning("Gắn CV vào job {JobId} trùng do thao tác đồng thời, thử lại", job.JobId);
                foreach (var a in newApps.Values) db.Entry(a).State = EntityState.Detached;
            }
            catch (DbUpdateException ex) when (IsSql(ex, 2601, 2627, 547))
            {
                // 547: CV vừa bị xóa giữa lúc kiểm tra và lúc lưu (hiếm)
                logger.LogWarning("Gắn CV vào job {JobId} thất bại do thao tác đồng thời (SQL {Number})", job.JobId,
                    (ex.InnerException as SqlException)?.Number);
                return Problem(409, "Hồ sơ vừa được thay đổi bởi thao tác khác", "Vui lòng tải lại trang rồi thử lại.");
            }
        }

        // 5. Kết quả từng CV, theo đúng thứ tự gửi lên
        var items = cvIds.Select(cvId =>
        {
            if (!cvs.TryGetValue(cvId, out var cv))
                return new ApplicationItemResult
                {
                    CvId = cvId, Status = ApplicationItemStatuses.CvNotFound, Message = "Không tìm thấy CV."
                };

            var created = newApps.TryGetValue(cvId, out var app);
            return new ApplicationItemResult
            {
                CvId = cvId,
                FileName = cv.OriginalFileName,
                ApplicationId = created ? app!.ApplicationId : existing[cvId],
                Status = created ? ApplicationItemStatuses.Created : ApplicationItemStatuses.AlreadyExists,
                ExtractionStatus = cv.ExtractionStatus,
                Message = (created, cv.ExtractionStatus) switch
                {
                    (false, _) => "CV đã ứng tuyển vị trí này từ trước.",
                    (true, ExtractionStatuses.Failed) => "Đã gắn, nhưng CV không đọc được nội dung nên khi chấm sẽ báo lỗi.",
                    (true, ExtractionStatuses.Pending) => "Đã gắn. CV đang chờ đọc nội dung.",
                    _ => null
                }
            };
        }).ToList();

        var response = new CreateApplicationsResponse
        {
            JobId = job.JobId,
            JobTitle = job.Title,
            CreatedCount = newApps.Count,
            ExistingCount = items.Count(i => i.Status == ApplicationItemStatuses.AlreadyExists),
            FailedCount = items.Count(i => i.Status == ApplicationItemStatuses.CvNotFound),
            Items = items
        };

        // Chỉ ghi số lượng và mã job; không ghi tên file (thường chứa tên ứng viên)
        logger.LogInformation("Gắn CV vào job {JobId}: {Created} mới, {Existing} đã có, {Failed} không tìm thấy",
            job.JobId, response.CreatedCount, response.ExistingCount, response.FailedCount);

        return TypedResults.Ok(response);
    }

    // ---------- GET /api/applications?jobId=&cvId= ----------
    private static async Task<Results<Ok<List<ApplicationDto>>, ProblemHttpResult>> ListAsync(
        SmartCvDbContext db, CancellationToken ct, int? jobId = null, Guid? cvId = null)
    {
        // Bắt buộc lọc: không trả toàn bộ hồ sơ của mọi job trong một lần
        if (jobId is null && cvId is null)
            return Problem(400, "Thiếu điều kiện lọc", "Hãy truyền jobId hoặc cvId, ví dụ /api/applications?jobId=1.");

        var query = db.Applications.AsNoTracking().AsQueryable();
        if (jobId is not null) query = query.Where(a => a.JobId == jobId);
        if (cvId is not null) query = query.Where(a => a.CvId == cvId);

        var rows = await query
            .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.ApplicationId)
            .Select(a => new ApplicationDto
            {
                ApplicationId = a.ApplicationId,
                JobId = a.JobId,
                JobTitle = a.Job.Title,
                CvId = a.CvId,
                FileName = a.Cv.OriginalFileName,
                CandidateName = a.Cv.Candidate != null ? a.Cv.Candidate.FullName : null,
                ExtractionStatus = a.Cv.ExtractionStatus,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync(ct);

        // Database lưu giờ UTC (datetime2 không kèm múi giờ): đánh dấu UTC để JSON có hậu tố "Z"
        var list = rows.Select(r => new ApplicationDto
        {
            ApplicationId = r.ApplicationId, JobId = r.JobId, JobTitle = r.JobTitle, CvId = r.CvId, FileName = r.FileName,
            CandidateName = r.CandidateName, ExtractionStatus = r.ExtractionStatus,
            CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)
        }).ToList();

        return TypedResults.Ok(list);
    }

    // ---------- DELETE /api/applications/{id} ----------
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        int id, SmartCvDbContext db, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var application = await db.Applications.FirstOrDefaultAsync(a => a.ApplicationId == id, ct);
        if (application is null) return TypedResults.NotFound();

        // Hồ sơ đã có kết quả chấm, nhãn HR hoặc quyết định thì không xóa, để giữ lịch sử.
        var used = await db.Evaluations.AnyAsync(e => e.ApplicationId == id, ct)
                   || await db.HrLabels.AnyAsync(h => h.ApplicationId == id, ct)
                   || await db.ApplicationEvents.AnyAsync(e => e.ApplicationId == id, ct);
        if (used)
            return UsedProblem();

        db.Applications.Remove(application);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsSql(ex, 547))
        {
            // Lượt chấm / nhãn HR vừa được thêm sau bước kiểm tra trên: khóa ngoại (Restrict) chặn xóa
            loggerFactory.CreateLogger("SmartCv.Api.Applications")
                .LogWarning("Bỏ gắn hồ sơ {ApplicationId} bị chặn do vừa có kết quả chấm / nhãn HR", id);
            return UsedProblem();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (await db.Applications.AsNoTracking().AnyAsync(a => a.ApplicationId == id, ct))
                return Problem(409, "Hồ sơ vừa được đổi", "Hãy tải lại dữ liệu trước khi bỏ gắn hồ sơ.");
            return TypedResults.NotFound();
        }

        return TypedResults.NoContent();
    }

    private static ProblemHttpResult UsedProblem() =>
        Problem(409, "Không thể bỏ gắn", "Hồ sơ này đã được chấm điểm, gán nhãn hoặc có quyết định HR, cần giữ lại để đối chiếu.");

    private static bool IsSql(DbUpdateException ex, params int[] numbers) =>
        ex.InnerException is SqlException sql && numbers.Contains(sql.Number);

    private static ProblemHttpResult Problem(int status, string title, string detail) =>
        TypedResults.Problem(title: title, detail: detail, statusCode: status);
}
