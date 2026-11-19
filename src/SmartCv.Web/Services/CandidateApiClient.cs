using SmartCv.Core.Contracts;
using SmartCv.Core.Cvs;

namespace SmartCv.Web.Services;

/// <summary>B23 only: read candidates for explicit attachment, no merge/erase operations.</summary>
public sealed class CandidateApiClient(HttpClient http, ILogger<CandidateApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<CandidatePageDto>> ListAsync(string search, int page = 1, CancellationToken ct = default)
    {
        var normalized = CvListQuery.NormalizeSearch(search);
        if (normalized is null || normalized.Length is < 2 or > 100 || page < 1)
            return Task.FromResult(ApiResult<CandidatePageDto>.Failure("Từ khóa cần từ 2 đến 100 ký tự.", 400));
        return GetPrivateAsync<CandidatePageDto>($"api/candidates?search={Uri.EscapeDataString(normalized)}&page={page}&pageSize=10", "Candidate search", ct);
    }
}
