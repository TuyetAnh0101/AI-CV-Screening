using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// Các API liên quan đến CV:
///   POST /api/cvs/upload      tải một hoặc nhiều file (A4–A6)
///   GET  /api/cvs/{id}        thông tin CV, trạng thái đọc chữ, các job đã ứng tuyển (A6)
///   GET  /api/cvs/{id}/file   xem / tải file gốc (A6)
///   GET  /api/cvs/{id}/text   chữ đã đọc + chỉ số chất lượng (A42); 410 khi CV đã ẩn danh hóa
///   POST /api/cvs/{id}/extract đọc lại chữ (A7), ví dụ khi lần trước lỗi do thiếu dữ liệu OCR
/// </summary>
public static class CvEndpoints
{
    /// <summary>
    /// Đăng ký POST /api/cvs/upload.
    /// An toàn mặc định: endpoint chỉ được tạo khi CvUpload:Endpoint:Enabled = true (appsettings.Development.json).
    /// Khi chưa có xác thực (M6), bật ở môi trường khác Development làm ứng dụng dừng ngay lúc khởi động,
    /// để API tải CV không bao giờ bị mở công khai do cấu hình nhầm.
    /// </summary>
    public static WebApplication MapCvEndpoints(this WebApplication app)
    {
        var endpointOptions = app.Services.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value;
        var uploadOptions = app.Services.GetRequiredService<IOptions<CvUploadOptions>>().Value;

        if (!endpointOptions.Enabled)
        {
            app.Logger.LogInformation(
                "API tải CV (POST /api/cvs/upload) đang TẮT: CvUpload:Endpoint:Enabled = false.");
            return app;
        }

        if (!app.Environment.IsDevelopment())
        {
            // M6: khi đã có xác thực, bỏ đoạn kiểm tra này và thêm .RequireAuthorization(...) cho endpoint bên dưới.
            throw new InvalidOperationException(
                $"Không được bật API tải CV ở môi trường '{app.Environment.EnvironmentName}' khi chưa có xác thực (M6). " +
                "Đặt CvUpload:Endpoint:Enabled = false, hoặc chỉ bật trong môi trường Development.");
        }

        var group = app.MapGroup("/api/cvs").WithTags("CVs");

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
            // Antiforgery chỉ có tác dụng khi trình duyệt tự gửi cookie đăng nhập. API chưa có đăng nhập và
            // được Blazor gọi từ phía máy chủ bằng HttpClient (không cookie), nên không có phiên để bị CSRF.
            // Truy cập được kiểm soát bằng việc chỉ bật ở Development (ở trên) cho đến khi có xác thực (M6).
            .DisableAntiforgery();

        // A6: xem lại CV đã lưu. Hai API này trả dữ liệu cá nhân (tên, nội dung CV), nên dùng chung chốt chặn ở trên:
        // chỉ có ở Development cho đến khi có xác thực (M6). M6: thêm .RequireAuthorization(...) cho cả nhóm.
        group.MapGet("/{id:guid}", GetCvAsync)
            .WithName("GetCv")
            .WithSummary("Thông tin một CV, trạng thái đọc chữ và các job đã ứng tuyển");

        group.MapGet("/{id:guid}/file", GetCvFileAsync)
            .WithName("GetCvFile")
            .WithSummary("Xem file CV gốc (thêm ?download=true để tải về)")
            .ProducesProblem(StatusCodes.Status404NotFound);

        // A42: chữ đã đọc + chất lượng cho B38 "Xem chữ đã đọc". Trả nội dung CV nên cùng chốt chặn Development ở trên;
        // CvPrivacyReadMiddleware giữ khóa đọc A39 và đặt Cache-Control: no-store cho mọi route /api/cvs.
        group.MapGet("/{id:guid}/text", GetCvTextAsync)
            .WithName("GetCvText")
            .WithSummary("Chữ đã đọc từ CV và chỉ số chất lượng (cảnh báo CV ảnh mờ có thể đọc sai)")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone);

        // A7: đọc lại chữ. Cùng chốt chặn Development như trên; giới hạn tần suất như API tải lên vì OCR tốn CPU.
        group.MapPost("/{id:guid}/extract", ReExtractAsync)
            .WithName("ReExtractCv")
            .WithSummary("Đặt lại trạng thái Pending và xếp CV vào hàng đợi đọc chữ")
            .RequireRateLimiting(CvUploadRateLimiting.PolicyName)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            // Không có cookie/phiên đăng nhập nên không có nguy cơ CSRF (giống API tải lên ở trên).
            .DisableAntiforgery();

        return app;
    }

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

        var result = await uploadService.UploadAsync(uploadFiles, source, cancellationToken);
        return TypedResults.Ok(result);
    }
}
