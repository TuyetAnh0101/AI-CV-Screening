using System.Net;
using System.Net.Http.Json;

namespace SmartCv.Web.Services;

/// <summary>
/// Lớp cha của mọi service gọi API. Gom chung việc gửi request và đổi lỗi kỹ thuật thành câu tiếng Việt dễ hiểu,
/// để các service con (JobApiClient, CvApiClient...) chỉ còn 1–2 dòng mỗi hàm.
/// </summary>
public abstract class ApiClientBase(HttpClient http, ILogger logger)
{
    protected HttpClient Http { get; } = http;

    protected Task<ApiResult<T>> GetAsync<T>(string url, CancellationToken ct = default) =>
        SendAsync<T>(() => Http.GetAsync(url, ct), url, ct);

    protected Task<ApiResult<T>> PostAsync<T>(string url, HttpContent content, CancellationToken ct = default) =>
        SendAsync<T>(() => Http.PostAsync(url, content, ct), url, ct);

    protected Task<ApiResult<T>> PostJsonAsync<T>(string url, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => Http.PostAsJsonAsync(url, body, ct), url, ct);

    private async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, string url, CancellationToken ct)
    {
        try
        {
            using var response = await send();

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<T>(ct);
                return data is null
                    ? ApiResult<T>.Failure("API trả về dữ liệu rỗng.", (int)response.StatusCode)
                    : ApiResult<T>.Success(data);
            }

            var message = await ReadErrorMessageAsync(response, ct);
            logger.LogWarning("Gọi API {Url} lỗi {Status}: {Message}", url, (int)response.StatusCode, message);
            return ApiResult<T>.Failure(message, (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Không kết nối được API khi gọi {Url}", url);
            return ApiResult<T>.Failure("Không kết nối được máy chủ API. Hãy kiểm tra API đã chạy và ApiBaseUrl đúng cổng.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Gọi API {Url} quá thời gian chờ", url);
            return ApiResult<T>.Failure("Máy chủ API phản hồi quá lâu. Vui lòng thử lại.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogError(ex, "Dữ liệu từ {Url} không đúng định dạng", url);
            return ApiResult<T>.Failure("Dữ liệu API trả về không đúng định dạng mong đợi.");
        }
    }

    /// <summary>Đọc thông báo lỗi. API trả lỗi theo chuẩn ProblemDetails (title, detail).</summary>
    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(ct);
            if (!string.IsNullOrWhiteSpace(problem?.Detail)) return problem.Detail!;
            if (!string.IsNullOrWhiteSpace(problem?.Title)) return problem.Title!;
        }
        catch
        {
            // Nội dung lỗi không phải JSON: dùng thông báo mặc định bên dưới
        }

        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => "Không tìm thấy dữ liệu hoặc API chưa có đường dẫn này.",
            HttpStatusCode.BadRequest => "Dữ liệu gửi lên không hợp lệ.",
            HttpStatusCode.RequestEntityTooLarge => "Dữ liệu gửi lên quá lớn.",
            HttpStatusCode.UnsupportedMediaType => "Định dạng dữ liệu gửi lên không được hỗ trợ.",
            HttpStatusCode.TooManyRequests => "Đang có quá nhiều yêu cầu. Vui lòng chờ một lát rồi thử lại.",
            HttpStatusCode.InternalServerError => "Máy chủ API gặp lỗi. Xem log ở terminal đang chạy API.",
            _ => $"Gọi API thất bại (mã {(int)response.StatusCode})."
        };
    }

    private sealed record ApiProblem(string? Title, string? Detail, int? Status);
}
