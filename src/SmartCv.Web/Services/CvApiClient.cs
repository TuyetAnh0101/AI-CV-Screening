using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Services;

/// <summary>Gọi API CV. Giới hạn đọc từ mục "CvUpload" trong appsettings.json của Web (phải khớp với API).</summary>
public sealed class CvApiClient(HttpClient http, IOptions<CvUploadOptions> options, ILogger<CvApiClient> logger)
    : ApiClientBase(http, logger)
{
    public CvUploadOptions Options { get; } = options.Value;

    /// <summary>
    /// POST /api/cvs/upload: gửi một hoặc nhiều file, mỗi file một phần tên "files".
    /// Người gọi phải tự chia nhóm theo <see cref="CvUploadOptions.MaxFilesPerRequest"/> (trang Upload làm việc này).
    /// B38 (A38): mỗi request kèm "sourceChannel" (một trong 5 nguồn) và "consentConfirmed" = true do HR xác nhận;
    /// API từ chối CẢ request (400) nếu thiếu. Không bao giờ tự điền: thiếu thì không gửi gì.
    /// </summary>
    public async Task<ApiResult<CvUploadResponse>> UploadAsync(IReadOnlyList<IBrowserFile> files, string sourceChannel,
        bool consentConfirmed, CancellationToken ct = default)
    {
        if (!CvSourceChannels.IsValid(sourceChannel))
            throw new ArgumentException($"sourceChannel phải là một trong: {string.Join(", ", CvSourceChannels.All)}.", nameof(sourceChannel));
        if (!consentConfirmed)
            throw new ArgumentException("Chưa có xác nhận ứng viên đã đồng ý: không gửi CV.", nameof(consentConfirmed));

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(sourceChannel), "sourceChannel");
        content.Add(new StringContent("true"), "consentConfirmed");

        foreach (var file in files)
        {
            // KHÔNG mở stream ở đây: BrowserFileContent chỉ mở stream của file khi HttpClient gửi tới phần đó,
            // nên mỗi lúc chỉ một file truyền qua circuit (mở trước cả nhóm làm circuit nghẽn rồi chết sau 1 phút).
            content.Add(new BrowserFileContent(file, Options.MaxFileSizeBytes, ct), "files", file.Name);
        }

        return await PostAsync<CvUploadResponse>("api/cvs/upload", content, ct);
    }

    /// <summary>GET /api/cvs/{id}: trạng thái đọc chữ + textQuality (A42). Trang Upload chỉ lấy trạng thái, không giữ chữ.</summary>
    public Task<ApiResult<CvDetailDto>> GetCvAsync(Guid cvId, CancellationToken ct = default) =>
        GetAsync<CvDetailDto>($"api/cvs/{cvId}", ct);

    /// <summary>B38 (A42): GET /api/cvs/{id}/text — chữ đã đọc + chỉ số chất lượng; 410 khi CV đã ẩn danh hóa (A39).</summary>
    public Task<ApiResult<CvTextDto>> GetTextAsync(Guid cvId, CancellationToken ct = default) =>
        GetAsync<CvTextDto>($"api/cvs/{cvId}/text", ct);

    public Task<ApiResult<CvPageDto>> ListAsync(CvLibraryFilter filter, CancellationToken ct = default)
    {
        var (valid, error) = filter.Validate();
        return valid is null ? Task.FromResult(ApiResult<CvPageDto>.Failure(error!, 400))
            : GetPrivateAsync<CvPageDto>("api/cvs?" + valid.ApiQuery, "CV library", ct);
    }

    public Task<ApiResult<CandidateSuggestionDto>> GetCandidateSuggestionAsync(Guid id, CancellationToken ct = default) =>
        GetPrivateAsync<CandidateSuggestionDto>($"api/cvs/{id}/candidate-suggestion", "CV candidate suggestion", ct);

    public Task<ApiResult<AttachCandidateResponse>> AttachCandidateAsync(Guid id, AttachCandidateRequest request, CancellationToken ct = default) =>
        PutPrivateJsonAsync<AttachCandidateResponse>($"api/cvs/{id}/candidate", request, ct);

    public string FileUrl(Guid id) => new Uri(Http.BaseAddress!, $"api/cvs/{id}/file").AbsoluteUri;
}
