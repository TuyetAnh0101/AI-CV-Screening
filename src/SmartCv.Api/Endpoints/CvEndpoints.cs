using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Candidates;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Cvs;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using SmartCv.Core.Privacy;
using SmartCv.Core.Storage;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// Các API liên quan đến CV:
///   GET  /api/cvs             kho CV: tìm kiếm + lọc + phân trang (A23)
///                             ?search=&amp;jobId=&amp;stage=&amp;source=&amp;status=&amp;page=&amp;pageSize=
///   POST /api/cvs/upload      tải một hoặc nhiều file (A4–A6)
///   GET  /api/cvs/{id}        thông tin CV, trạng thái đọc chữ, các job đã ứng tuyển (A6)
///   GET  /api/cvs/{id}/file   xem / tải file gốc (A6)
///   GET  /api/cvs/{id}/text   chữ đã đọc + chỉ số chất lượng (A42); 410 khi CV đã ẩn danh hóa
///   POST /api/cvs/{id}/extract đọc lại chữ (A7), ví dụ khi lần trước lỗi do thiếu dữ liệu OCR
/// </summary>
public static partial class CvEndpoints
{
    /// <summary>
    /// Đăng ký POST /api/cvs/upload.
    /// An toàn mặc định: endpoint chỉ được tạo khi CvUpload:Endpoint:Enabled = true (appsettings.Development.json).
    /// A29: mọi môi trường đều yêu cầu JWT và quyền HR/Admin qua MapSmartCvApi.
    /// </summary>
    public static IEndpointRouteBuilder MapCvEndpoints(this IEndpointRouteBuilder app)
    {
        var endpointOptions = app.ServiceProvider.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value;
        var uploadOptions = app.ServiceProvider.GetRequiredService<IOptions<CvUploadOptions>>().Value;

        if (!endpointOptions.Enabled)
        {
            app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CvEndpoints").LogInformation(
                "API tải CV (POST /api/cvs/upload) đang TẮT: CvUpload:Endpoint:Enabled = false.");
            return app;
        }

        var group = app.MapGroup("/api/cvs").WithTags("CVs");

        // A29 supplies HR/Admin authorization; CvPrivacyReadMiddleware keeps the A39 read lease and no-store.
        group.MapGet("/", ListAsync)
            .WithName("ListCvs")
            .WithSummary("Kho CV: tìm theo tên file, tên / email ứng viên, nội dung CV; lọc theo job, giai đoạn, nguồn, trạng thái")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/upload", UploadAsync)
            .WithName("UploadCvs")
            .WithSummary("Tải lên một hoặc nhiều file CV (PDF, PNG, JPG)")
            .WithDescription("Gửi dạng multipart/form-data, mỗi file một phần tên \"files\".")
            .Accepts<IFormFileCollection>("multipart/form-data")
            // Giới hạn dung lượng CHỈ cho endpoint này (= MaxFileSizeMB × MaxFilesPerRequest + 1 MiB, mặc định 201 MiB).
            // Các endpoint khác giữ mặc định của Kestrel (30 MB). Vượt giới hạn: 413.
            .WithMetadata(new RequestSizeLimitAttribute(uploadOptions.MaxRequestBytes))
            .WithFormOptions(multipartBodyLengthLimit: uploadOptions.MaxRequestBytes)
            .RequireRateLimiting(CvUploadRateLimiting.PolicyName)
            // Ghi rõ trong OpenAPI: 400 (ProblemDetails) khi không có file / quá số file,
            // 413 khi request vượt giới hạn dung lượng (do máy chủ trả, không có nội dung),
            // 429 (ProblemDetails) khi vượt giới hạn tần suất.
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            // JWT is supplied explicitly as a Bearer header; ambient authentication cookies are not used.
            .DisableAntiforgery();

        // A6: CV metadata and content inherit the authenticated HR/Admin boundary.
        group.MapGet("/{id:guid}", GetCvAsync)
            .WithName("GetCv")
            .WithSummary("Thông tin một CV, trạng thái đọc chữ và các job đã ứng tuyển");

        group.MapGet("/{id:guid}/file", GetCvFileAsync)
            .WithName("GetCvFile")
            .WithSummary("Xem file CV gốc (thêm ?download=true để tải về)")
            .ProducesProblem(StatusCodes.Status404NotFound);

        // A42: chữ đã đọc + chất lượng cho B38, cùng quyền HR/Admin;
        // CvPrivacyReadMiddleware giữ khóa đọc A39 và đặt Cache-Control: no-store cho mọi route /api/cvs.
        group.MapGet("/{id:guid}/text", GetCvTextAsync)
            .WithName("GetCvText")
            .WithSummary("Chữ đã đọc từ CV và chỉ số chất lượng (cảnh báo CV ảnh mờ có thể đọc sai)")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone);

        // A7: đọc lại chữ. Cùng quyền HR/Admin; giới hạn tần suất như API tải lên vì OCR tốn CPU.
        group.MapPost("/{id:guid}/extract", ReExtractAsync)
            .WithName("ReExtractCv")
            .WithSummary("Đặt lại trạng thái Pending và xếp CV vào hàng đợi đọc chữ")
            .RequireRateLimiting(CvUploadRateLimiting.PolicyName)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            // Không có cookie/phiên đăng nhập nên không có nguy cơ CSRF (giống API tải lên ở trên).
            .DisableAntiforgery();

        // A24: same HR/Admin policy and A39 privacy middleware as the existing CV API.
        group.MapPut("/{id:guid}/candidate", AttachCandidateAsync)
            .WithName("AttachCandidate").Accepts<AttachCandidateRequest>("application/json").AddEndpointFilter<CandidateApiFilter>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        group.MapGet("/{id:guid}/candidate-suggestion", CandidateSuggestionAsync)
            .WithName("CandidateSuggestion").AddEndpointFilter<CandidateApiFilter>()
            .ProducesProblem(404).ProducesProblem(410);
        return app;
    }

    // ---------- GET /api/cvs (A23: kho CV) ----------
    // Tìm bằng LIKE '%từ khóa%' (EF dịch string.Contains và tự thoát %, _, [ trong từ khóa). Theo collation mặc định của
    // SQL Server: không phân biệt hoa thường, có phân biệt dấu. Vài trăm–vài nghìn CV chưa cần Full-Text Search (xem
    // hướng dẫn A23); ElapsedMs trả về để theo dõi. Chỉ đọc: không ghi database, không ghi log từ khóa hay nội dung CV.
    private static async Task<Results<Ok<CvPageDto>, ProblemHttpResult>> ListAsync(
        HttpContext http, SmartCvDbContext db, ILogger<CvListQuery> logger, CancellationToken ct)
    {
        var parameters = http.Request.Query.ToDictionary(p => p.Key, p => p.Value.Select(v => v ?? "").ToArray(), StringComparer.OrdinalIgnoreCase);
        var (query, errors) = CvListQuery.Parse(parameters);
        if (query is null)
            return TypedResults.Problem(title: "Bộ lọc không hợp lệ", detail: string.Join(" ", errors.Values.SelectMany(v => v)),
                statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["errors"] = errors });

        var watch = Stopwatch.StartNew();
        var cvs = db.Cvs.AsNoTracking();

        if (query.Search is { } search)
        {
            var phone = CandidateRules.NormalizePhone(search); // A24: formatted VN numbers match normalized contacts.
            // Hồ sơ đã ẩn danh: không còn tên, email, chữ và không được tìm ra (kể cả theo tên file thay thế "Hồ sơ đã ẩn danh").
            cvs = cvs.Where(c => c.AnonymizedAt == null && (
                c.OriginalFileName.Contains(search)
                || (c.Candidate != null && (c.Candidate.FullName.Contains(search)
                                            || (c.Candidate.Email != null && c.Candidate.Email.Contains(search))
                                            || (phone != null && c.Candidate.PhoneNumber == phone)))
                || (c.ExtractedText != null && c.ExtractedText.Contains(search))));
        }

        if (query.JobId is { } jobId)
            cvs = query.Stage is { } jobStage
                ? cvs.Where(c => c.Applications.Any(a => a.JobId == jobId && a.Stage == jobStage))
                : cvs.Where(c => c.Applications.Any(a => a.JobId == jobId));
        else if (query.Stage is { } stage)
            cvs = cvs.Where(c => c.Applications.Any(a => a.Stage == stage));

        if (query.Source is { } source) cvs = cvs.Where(c => c.SourceChannel == source);

        if (query.Status is { } status)
            cvs = status == CvListQuery.AnonymizedStatus
                ? cvs.Where(c => c.AnonymizedAt != null)
                : cvs.Where(c => c.AnonymizedAt == null && c.ExtractionStatus == status);

        // B24: CV chưa gắn ứng viên phải tìm được trên toàn kho (không lọc giả trên một trang đã phân trang).
        // CV đã ẩn danh hóa không còn liên kết người và không gắn lại được, nên không thuộc "chưa gắn".
        if (query.Candidate == CvListQuery.UnattachedCandidate) cvs = cvs.Where(c => c.CandidateId == null && c.AnonymizedAt == null);
        else if (query.Candidate == CvListQuery.AttachedCandidate) cvs = cvs.Where(c => c.CandidateId != null);

        // 1) Mã CV của trang: lần quét duy nhất có điều kiện tìm/lọc (LIKE trên ExtractedText là phần tốn thời gian).
        var ids = await cvs
            .OrderByDescending(c => c.UploadedAt).ThenBy(c => c.CvId)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(c => c.CvId)
            .ToListAsync(ct);

        // 2) Trang chưa đầy (và có dòng, hoặc là trang 1) là trang cuối: tổng = số dòng trước nó + số dòng của nó,
        //    không cần quét lại. Trường hợp chậm nhất của hướng dẫn ("khongcotrongcv", 0 kết quả) chỉ còn một lần quét.
        var total = ids.Count < query.PageSize && (ids.Count > 0 || query.Page == 1)
            ? query.Skip + ids.Count
            : await cvs.CountAsync(ct);

        // 3) Chi tiết theo khóa chính (không lặp lại điều kiện tìm), giữ đúng thứ tự của bước 1.
        var details = ids.Count == 0 ? [] : await db.Cvs.AsNoTracking()
            .Where(c => ids.Contains(c.CvId))
            .Select(c => new
            {
                c.CvId, c.UploadedAt, c.ExtractionStatus, c.SourceChannel, c.RetainUntil, c.AnonymizedAt,
                // Phòng hờ dữ liệu cũ: CV đã ẩn danh không bao giờ trả tên file gốc, ứng viên hay email.
                FileName = c.AnonymizedAt == null ? c.OriginalFileName : RetentionRules.AnonymizedFileName,
                CandidateId = c.AnonymizedAt == null ? c.CandidateId : null,
                CandidateName = c.AnonymizedAt == null && c.Candidate != null ? c.Candidate.FullName : null,
                CandidateEmail = c.AnonymizedAt == null && c.Candidate != null ? c.Candidate.Email : null,
                Applications = c.Applications.OrderBy(a => a.CreatedAt).ThenBy(a => a.ApplicationId).Select(a => new
                {
                    a.ApplicationId, a.JobId, JobTitle = a.Job.Title, a.Stage, a.RejectReason, a.StageUpdatedAt,
                    // Điểm của lần chấm SÀNG LỌC xong gần nhất; lượt đánh giá prompt không tính.
                    Latest = a.Evaluations
                        .Where(e => e.Status == EvaluationStatuses.Completed && e.Run.RunType == RunTypes.Screening)
                        .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.EvaluationId)
                        .Select(e => new { e.FinalScore, e.FitLabel, e.CreatedAt })
                        .FirstOrDefault()
                }).ToList()
            })
            .AsSplitQuery()
            .ToListAsync(ct);
        var order = ids.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
        var rows = details.OrderBy(r => order[r.CvId]).ToList();

        watch.Stop();
        if (watch.ElapsedMilliseconds > 1500)
            logger.LogWarning("Tìm kho CV chậm: {Ms} ms, {Total} kết quả. Cân nhắc Full-Text Search (A23).", watch.ElapsedMilliseconds, total);

        return TypedResults.Ok(new CvPageDto
        {
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = total,
            TotalPages = CvListQuery.TotalPages(total, query.PageSize),
            ElapsedMs = watch.ElapsedMilliseconds,
            Items = rows.Select(r => new CvListItemDto
            {
                CvId = r.CvId,
                FileName = r.FileName,
                UploadedAt = DateTime.SpecifyKind(r.UploadedAt, DateTimeKind.Utc),
                ExtractionStatus = r.ExtractionStatus,
                SourceChannel = r.SourceChannel,
                SourceChannelName = CvSourceChannels.DisplayName(r.SourceChannel),
                RetainUntil = Utc(r.RetainUntil),
                AnonymizedAt = Utc(r.AnonymizedAt),
                CandidateId = r.CandidateId,
                CandidateName = r.CandidateName,
                CandidateEmail = r.CandidateEmail,
                Applications = r.Applications.Select(a => new CvJobStatusDto
                {
                    ApplicationId = a.ApplicationId,
                    JobId = a.JobId,
                    JobTitle = a.JobTitle,
                    Stage = a.Stage,
                    RejectReason = a.RejectReason,
                    StageUpdatedAt = Utc(a.StageUpdatedAt),
                    LatestFinalScore = a.Latest?.FinalScore,
                    LatestFitLabel = a.Latest?.FitLabel,
                    LatestEvaluatedAt = a.Latest is null ? null : DateTime.SpecifyKind(a.Latest.CreatedAt, DateTimeKind.Utc)
                }).ToList()
            }).ToList()
        });
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    // ---------- POST /api/cvs/{id}/extract ----------
    private static async Task<Results<Accepted<CvExtractionAcceptedDto>, NotFound, ProblemHttpResult>> ReExtractAsync(
        Guid id, SmartCvDbContext db, ICvExtractionQueue queue, CancellationToken ct)
    {
        var cv = await db.Cvs.FirstOrDefaultAsync(c => c.CvId == id, ct);
        if (cv is null) return TypedResults.NotFound();
        if (cv.AnonymizedAt is not null) return TypedResults.Problem(statusCode: 410, title: "CV đã được ẩn danh hóa");

        // Đang Pending (chờ hoặc đang đọc): không đặt lại, chỉ bảo đảm có trong hàng đợi (hàng đợi tự bỏ trùng)
        if (cv.ExtractionStatus != ExtractionStatuses.Pending)
        {
            cv.ExtractionStatus = ExtractionStatuses.Pending;
            cv.ExtractionError = null;
            await db.SaveChangesAsync(ct);
        }

        await queue.EnqueueAsync(id, CancellationToken.None);

        return TypedResults.Accepted($"/api/cvs/{id}",
            new CvExtractionAcceptedDto { CvId = id, ExtractionStatus = cv.ExtractionStatus });
    }

    // ---------- GET /api/cvs/{id} ----------
    private static async Task<Results<Ok<CvDetailDto>, NotFound>> GetCvAsync(
        Guid id, SmartCvDbContext db, ICvFileStorage storage, CancellationToken ct)
    {
        var cv = await db.Cvs.AsNoTracking()
            .Where(c => c.CvId == id)
            .Select(c => new
            {
                c.CvId, c.CandidateId, CandidateName = c.Candidate != null ? c.Candidate.FullName : null,
                c.OriginalFileName, c.ContentType, c.FileSizeBytes, c.UploadedAt, c.StoredPath,
                c.ExtractionStatus, c.ExtractionMethod, c.ExtractionError, c.ExtractedText,
                c.SourceChannel, c.ConsentConfirmedAt, c.RetainUntil, c.AnonymizedAt,
                Applications = c.Applications
                    .OrderBy(a => a.JobId)
                    .Select(a => new CvApplicationDto
                    {
                        ApplicationId = a.ApplicationId, JobId = a.JobId, JobTitle = a.Job.Title, CreatedAt = a.CreatedAt
                    }).ToList()
            })
            .FirstOrDefaultAsync(ct);

        if (cv is null) return TypedResults.NotFound();

        // Không trả StoredPath (đường dẫn nội bộ trên máy chủ)
        return TypedResults.Ok(new CvDetailDto
        {
            CvId = cv.CvId,
            CandidateId = cv.AnonymizedAt is null ? cv.CandidateId : null,
            CandidateName = cv.AnonymizedAt is null ? cv.CandidateName : null,
            OriginalFileName = cv.AnonymizedAt is null ? cv.OriginalFileName : SmartCv.Core.Privacy.RetentionRules.AnonymizedFileName,
            ContentType = cv.ContentType,
            FileSizeBytes = cv.FileSizeBytes,
            UploadedAt = DateTime.SpecifyKind(cv.UploadedAt, DateTimeKind.Utc),
            FileAvailable = cv.AnonymizedAt is null && SafeExists(storage, cv.StoredPath),
            SourceChannel = cv.SourceChannel,
            SourceChannelName = CvSourceChannels.DisplayName(cv.SourceChannel),
            ConsentConfirmedAt = cv.ConsentConfirmedAt is { } consent ? DateTime.SpecifyKind(consent, DateTimeKind.Utc) : null,
            RetainUntil = cv.RetainUntil is { } retain ? DateTime.SpecifyKind(retain, DateTimeKind.Utc) : null,
            AnonymizedAt = cv.AnonymizedAt is { } anonymized ? DateTime.SpecifyKind(anonymized, DateTimeKind.Utc) : null,
            ExtractionStatus = cv.ExtractionStatus,
            ExtractionMethod = cv.ExtractionMethod,
            ExtractionError = cv.AnonymizedAt is null ? cv.ExtractionError : null,
            ExtractedText = cv.AnonymizedAt is null ? cv.ExtractedText : null,
            // A42: chỉ đánh giá chữ của lần đọc đã xong; Pending (kể cả còn chữ cũ khi đọc lại) / Failed / ẩn danh → null
            TextQuality = cv.AnonymizedAt is null && cv.ExtractionStatus == ExtractionStatuses.Completed
                ? ToQualityDto(TextQuality.Assess(cv.ExtractedText, cv.ExtractionMethod))
                : null,
            Applications = cv.Applications
        });
    }

    // ---------- GET /api/cvs/{id}/text (A42) ----------
    private static async Task<Results<Ok<CvTextDto>, ProblemHttpResult>> GetCvTextAsync(Guid id, SmartCvDbContext db, CancellationToken ct)
    {
        // Chỉ đọc các cột cần (không có StoredPath); không ghi log nội dung CV
        var cv = await db.Cvs.AsNoTracking()
            .Where(c => c.CvId == id)
            .Select(c => new { c.CvId, c.OriginalFileName, c.ExtractionStatus, c.ExtractionMethod, c.ExtractionError, c.ExtractedText, c.AnonymizedAt })
            .FirstOrDefaultAsync(ct);

        if (cv is null)
            return TypedResults.Problem(title: "Không tìm thấy CV", detail: $"Không có CV mã {id}.", statusCode: StatusCodes.Status404NotFound);
        // A39: CV đã ẩn danh hóa không còn chữ hay tên file; trả 410 (cùng nội dung RetentionEndpoints.Gone) trước khi đụng tới nội dung
        if (cv.AnonymizedAt is not null)
            return TypedResults.Problem(title: "CV đã được ẩn danh hóa",
                detail: "File gốc và nội dung CV không còn được cung cấp. Điểm và lịch sử quyết định được giữ nguyên.",
                statusCode: StatusCodes.Status410Gone);

        var completed = cv.ExtractionStatus == ExtractionStatuses.Completed;
        return TypedResults.Ok(new CvTextDto
        {
            CvId = cv.CvId,
            OriginalFileName = cv.OriginalFileName,
            ExtractionStatus = cv.ExtractionStatus,
            ExtractionMethod = cv.ExtractionMethod,
            ExtractionError = cv.ExtractionError,
            // Pending có thể còn chữ của lần đọc trước (A7 đọc lại): không trả chữ cũ như thể là kết quả mới
            Text = completed ? cv.ExtractedText : null,
            Quality = completed ? ToQualityDto(TextQuality.Assess(cv.ExtractedText, cv.ExtractionMethod)) : null,
            StatusMessage = cv.ExtractionStatus switch
            {
                ExtractionStatuses.Completed => string.IsNullOrWhiteSpace(cv.ExtractedText)
                    ? "Đã đọc xong nhưng không có chữ nào: kiểm tra file gốc (có thể là ảnh trắng hoặc PDF chỉ có hình)."
                    : "Đã đọc xong.",
                ExtractionStatuses.Pending => "CV đang chờ đọc chữ, hãy tải lại sau ít giây.",
                ExtractionStatuses.Failed => "Không đọc được chữ từ CV. Có thể đọc lại bằng POST /api/cvs/{id}/extract.",
                _ => $"Trạng thái đọc chữ: {cv.ExtractionStatus}."
            }
        });
    }

    private static TextQualityDto ToQualityDto(TextQualityResult q) => new()
    {
        CharCount = q.CharCount,
        WordCount = q.WordCount,
        LetterRatio = q.LetterRatio,
        Level = q.Level,
        MaybeMisread = q.MaybeMisread,
        Warnings = [.. q.Warnings]
    };

    // ---------- GET /api/cvs/{id}/file ----------
    private static async Task<Results<FileStreamHttpResult, NotFound<ProblemDetails>, ProblemHttpResult>> GetCvFileAsync(
        Guid id, SmartCvDbContext db, ICvFileStorage storage, HttpContext http, CancellationToken ct, bool download = false)
    {
        // Chỉ tìm theo CvId: đường dẫn file lấy từ database, không bao giờ lấy từ request
        var cv = await db.Cvs.AsNoTracking()
            .Where(c => c.CvId == id)
            .Select(c => new { c.StoredPath, c.ContentType, c.OriginalFileName, c.AnonymizedAt })
            .FirstOrDefaultAsync(ct);

        if (cv is null)
            return TypedResults.NotFound(new ProblemDetails { Title = "Không tìm thấy CV", Status = 404 });
        if (cv.AnonymizedAt is not null) return TypedResults.Problem(statusCode: 410, title: "CV đã được ẩn danh hóa");

        Stream stream;
        try
        {
            stream = storage.OpenRead(cv.StoredPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            return TypedResults.NotFound(new ProblemDetails
            {
                Title = "File gốc không còn trên máy chủ",
                Detail = "CV có trong database nhưng không có file (ví dụ CV của dữ liệu demo).",
                Status = 404
            });
        }

        // Trình duyệt không được tự đoán lại loại file (ContentType là loại thật đã nhận diện ở A5)
        http.Response.Headers.XContentTypeOptions = "nosniff";

        // Không có tên tải về: trình duyệt mở xem trực tiếp (PDF, ảnh). Có: tải về với tên gốc
        // (ASP.NET tự mã hóa tên trong Content-Disposition, kể cả tên tiếng Việt / ký tự đặc biệt).
        return TypedResults.Stream(stream, cv.ContentType,
            fileDownloadName: download ? cv.OriginalFileName : null);
    }

    private static bool SafeExists(ICvFileStorage storage, string storedPath)
    {
        try { return storage.Exists(storedPath); }
        catch (InvalidOperationException) { return false; }   // đường dẫn không thuộc thư mục lưu CV
    }

    private static async Task<Results<Ok<CvUploadResponse>, ProblemHttpResult>> UploadAsync(
        IFormFileCollection files,
        [FromForm] string? sourceChannel,
        [FromForm] bool? consentConfirmed,
        ICvUploadService uploadService,
        IOptions<CvUploadOptions> options,
        CancellationToken cancellationToken)
    {
        var opt = options.Value;

        if (files.Count == 0)
        {
            return TypedResults.Problem(
                title: "Chưa chọn file",
                detail: "Hãy gửi ít nhất một file CV trong trường \"files\" (multipart/form-data).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (files.Count > opt.MaxFilesPerRequest)
        {
            return TypedResults.Problem(
                title: "Quá nhiều file",
                detail: $"Mỗi lần chỉ được tải tối đa {opt.MaxFilesPerRequest} file, bạn đã gửi {files.Count} file.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var (source, title, error) = CvUploadSource.Create(sourceChannel, consentConfirmed, DateTime.UtcNow);
        if (source is null) return TypedResults.Problem(title: title, detail: error, statusCode: StatusCodes.Status400BadRequest);

        // Chuyển IFormFile (của ASP.NET) sang CvUploadFile (của Core)
        var uploadFiles = files
            .Select(f => new CvUploadFile(f.FileName, f.ContentType, f.Length, f.OpenReadStream))
            .ToList();

        // var result = await uploadService.UploadAsync(uploadFiles, source, cancellationToken);
        // return TypedResults.Ok(result);

        try
        {
            var result = await uploadService.UploadAsync(
                uploadFiles, source, cancellationToken);

            return TypedResults.Ok(result);
        }
        catch (CvQuotaExceededException ex)
        {
            return TypedResults.Problem(
                statusCode: 409,
                title: "Đã vượt số lượng CV được phép",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "cv_quota_exceeded",
                    ["limit"] = ex.Limit,
                    ["used"] = ex.Used,
                    ["requested"] = ex.Requested
                });
        }

    }
}
