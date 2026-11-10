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
            // KHÔNG mở stream ở đây: BrowserFileContent chỉ mở stream của file khi HttpClient gửi tới phần đó,
            // nên mỗi lúc chỉ một file truyền qua circuit (mở trước cả nhóm làm circuit nghẽn rồi chết sau 1 phút).
            content.Add(new BrowserFileContent(file, Options.MaxFileSizeBytes, ct), "files", file.Name);
        }

        return await PostAsync<CvUploadResponse>("api/cvs/upload", content, ct);
    }
}
