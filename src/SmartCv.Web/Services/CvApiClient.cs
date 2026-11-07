using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;

namespace SmartCv.Web.Services;

/// <summary>Gọi API CV. Giới hạn đọc từ mục "CvUpload" trong appsettings.json của Web (phải khớp với API).</summary>
public sealed class CvApiClient(HttpClient http, IOptions<CvUploadOptions> options, ILogger<CvApiClient> logger)
    : ApiClientBase(http, logger)
{
    public CvUploadOptions Options { get; } = options.Value;

    /// <summary>POST /api/cvs/upload: gửi một hoặc nhiều file, mỗi file một phần tên "files".
    /// Người gọi phải tự chia nhóm theo <see cref="CvUploadOptions.MaxFilesPerRequest"/> (trang Upload làm việc này).</summary>
    public async Task<ApiResult<CvUploadResponse>> UploadAsync(IReadOnlyList<IBrowserFile> files, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();

        foreach (var file in files)
        {
            // Mặc định Blazor chỉ cho đọc 500 KB, nên phải truyền maxAllowedSize
            var stream = file.OpenReadStream(maxAllowedSize: Options.MaxFileSizeBytes, cancellationToken: ct);
            var part = new StreamContent(stream);
            part.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType);
            content.Add(part, "files", file.Name);
        }

        return await PostAsync<CvUploadResponse>("api/cvs/upload", content, ct);
    }
}
