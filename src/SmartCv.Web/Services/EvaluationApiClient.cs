using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

public sealed class EvaluationApiClient(HttpClient http, ILogger<EvaluationApiClient> logger) : ApiClientBase(http, logger)
{
    public Task<ApiResult<EvaluationDetailDto>> GetAsync(int id, CancellationToken ct = default) =>
        GetAsync<EvaluationDetailDto>($"api/evaluations/{id}", ct);

    public string CvUrl(Guid id) => new Uri(Http.BaseAddress!, $"api/cvs/{id}/file").AbsoluteUri;

    // Read headers only: do not buffer a large CV in the Blazor circuit.
    public async Task<ApiResult<bool>> CheckCvAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(CvUrl(id), HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode ? ApiResult<bool>.Success(true)
                : ApiResult<bool>.Failure("Không mở được CV.", (int)response.StatusCode);
        }
        catch (HttpRequestException) { return ApiResult<bool>.Failure("Không kết nối được máy chủ CV."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return ApiResult<bool>.Failure("Máy chủ CV phản hồi quá lâu."); }
    }
}
