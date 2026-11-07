using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.Forms;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>Gọi API CV. B2 dựng sẵn hàm tải lên (API A4) để B3 dùng ngay.</summary>
public sealed class CvApiClient(HttpClient http, ILogger<CvApiClient> logger) : ApiClientBase(http, logger)
{
    /// <summary>Khớp CvUpload:MaxFileSizeMB của API (10 MB).</summary>
    public const long MaxFileSizeBytes = 10 * 1024 * 1024;

    /// <summary>POST /api/cvs/upload: gửi một hoặc nhiều file, mỗi file một phần tên "files".</summary>
    public async Task<ApiResult<CvUploadResponse>> UploadAsync(IReadOnlyList<IBrowserFile> files, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();

        foreach (var file in files)
        {
            // Mặc định Blazor chỉ cho đọc 500 KB, nên phải truyền maxAllowedSize
            var stream = file.OpenReadStream(maxAllowedSize: MaxFileSizeBytes, cancellationToken: ct);
            var part = new StreamContent(stream);
            part.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType);
            content.Add(part, "files", file.Name);
        }

        return await PostAsync<CvUploadResponse>("api/cvs/upload", content, ct);
    }
}
