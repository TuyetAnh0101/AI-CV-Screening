using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Core.Uploads;

namespace SmartCv.Api.Endpoints;

/// <summary>Các API liên quan đến CV: /api/cvs/...</summary>
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

        return app;
    }

    private static async Task<Results<Ok<CvUploadResponse>, ProblemHttpResult>> UploadAsync(
        IFormFileCollection files,
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

        // Chuyển IFormFile (của ASP.NET) sang CvUploadFile (của Core)
        var uploadFiles = files
            .Select(f => new CvUploadFile(f.FileName, f.ContentType, f.Length, f.OpenReadStream))
            .ToList();

        var result = await uploadService.UploadAsync(uploadFiles, cancellationToken);
        return TypedResults.Ok(result);
    }
}
