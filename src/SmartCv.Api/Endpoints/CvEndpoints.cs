using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Core.Uploads;

namespace SmartCv.Api.Endpoints;

/// <summary>Các API liên quan đến CV: /api/cvs/...</summary>
public static class CvEndpoints
{
    public static IEndpointRouteBuilder MapCvEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cvs").WithTags("CVs");

        group.MapPost("/upload", UploadAsync)
            .WithName("UploadCvs")
            .WithSummary("Tải lên một hoặc nhiều file CV (PDF, PNG, JPG)")
            .WithDescription("Gửi dạng multipart/form-data, mỗi file một phần tên \"files\".")
            .Accepts<IFormFileCollection>("multipart/form-data")
            // Ghi rõ trong OpenAPI: 400 (ProblemDetails) khi không có file / quá số file,
            // 413 khi request vượt giới hạn dung lượng (do máy chủ Kestrel trả, không có nội dung).
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            // API được Blazor gọi từ phía máy chủ (không dùng cookie đăng nhập) nên tắt kiểm tra antiforgery.
            // Nếu không tắt, .NET báo lỗi "contains anti-forgery metadata, but a middleware was not found".
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
