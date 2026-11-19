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

    // B23: search queries and exception details may contain applicant information.
    protected Task<ApiResult<T>> GetPrivateAsync<T>(string url, string route, CancellationToken ct = default) =>
        SendAsync<T>(() => Http.GetAsync(url, ct), route, ct, sensitive: true);

    protected Task<ApiResult<T>> PutPrivateJsonAsync<T>(string url, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => Http.PutAsJsonAsync(url, body, ct), "CV candidate attachment", ct, sensitive: true);

    protected Task<ApiResult<T>> PostAsync<T>(string url, HttpContent content, CancellationToken ct = default) =>
        SendAsync<T>(() => Http.PostAsync(url, content, ct), url, ct);

    protected Task<ApiResult<T>> PostJsonAsync<T>(string url, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => Http.PostAsJsonAsync(url, body, ct), url, ct);

    protected Task<ApiResult<T>> PutJsonAsync<T>(string url, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => Http.PutAsJsonAsync(url, body, ct), url, ct);

    protected Task<ApiResult<T>> PatchJsonAsync<T>(string url, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => Http.PatchAsJsonAsync(url, body, ct), url, ct);

    // B21: DELETE 204 has no JSON body. Share the existing cancellation/error handling.
    protected Task<ApiResult<bool>> DeleteAsync(string url, CancellationToken ct = default) =>
        SendAsync<bool>(() => Http.DeleteAsync(url, ct), url, ct, delete: true);

    private async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, string url, CancellationToken ct, bool delete = false, bool sensitive = false)
    {
        try
        {
            using var response = await send();

            if (response.IsSuccessStatusCode)
            {
                if (delete) return ApiResult<T>.Success((T)(object)true);
                var data = await response.Content.ReadFromJsonAsync<T>(ct);
                return data is null
                    ? ApiResult<T>.Failure("API trả về dữ liệu rỗng.", (int)response.StatusCode)
                    : ApiResult<T>.Success(data);
            }

            var (message, code, errors) = await ReadErrorMessageAsync(response, ct);
            logger.LogWarning("Gọi API {Url} lỗi {Status}", url, (int)response.StatusCode);
            return ApiResult<T>.Failure(message, (int)response.StatusCode, code, RetryAfter(response), errors);
        }
        catch (HttpRequestException ex)
        {
            if (sensitive) logger.LogError("Không kết nối được API khi gọi {Route}", url);
            else logger.LogError(ex, "Không kết nối được API khi gọi {Url}", url);
            return ApiResult<T>.Failure("Không kết nối được máy chủ API. Hãy kiểm tra API đã chạy và ApiBaseUrl đúng cổng.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Gọi API {Url} quá thời gian chờ", url);
            return ApiResult<T>.Failure("Máy chủ API phản hồi quá lâu. Vui lòng thử lại.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            if (sensitive) logger.LogError("Dữ liệu từ {Route} không đúng định dạng", url);
            else logger.LogError(ex, "Dữ liệu từ {Url} không đúng định dạng", url);
            return ApiResult<T>.Failure("Dữ liệu API trả về không đúng định dạng mong đợi.");
        }
    }

    /// <summary>B39: Retry-After dạng số giây hoặc thời điểm; giá trị âm/không đọc được → null.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return delay is { } value && value >= TimeSpan.Zero ? value : null;
    }

    /// <summary>Đọc thông báo lỗi. API trả lỗi theo chuẩn ProblemDetails (title, detail, và "code" nếu có).</summary>
    private static async Task<(string Message, string? Code, Dictionary<string, string[]>? Errors)> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(ct);
            code = string.IsNullOrWhiteSpace(problem?.Code) ? null : problem.Code;
            if (!string.IsNullOrWhiteSpace(problem?.Detail)) return (problem.Detail!, code, problem.Errors);
            if (!string.IsNullOrWhiteSpace(problem?.Title)) return (problem.Title!, code, problem.Errors);
            return (DefaultMessage(response), code, problem?.Errors);
        }
        catch
        {
            // Nội dung lỗi không phải JSON: dùng thông báo mặc định bên dưới
        }

        return (DefaultMessage(response), code, null);
    }

    private static string DefaultMessage(HttpResponseMessage response) =>
        response.StatusCode switch
        {
            HttpStatusCode.NotFound => "Không tìm thấy dữ liệu hoặc API chưa có đường dẫn này.",
            HttpStatusCode.BadRequest => "Dữ liệu gửi lên không hợp lệ.",
            HttpStatusCode.RequestEntityTooLarge => "Dữ liệu gửi lên quá lớn.",
            HttpStatusCode.UnsupportedMediaType => "Định dạng dữ liệu gửi lên không được hỗ trợ.",
            HttpStatusCode.TooManyRequests => "Đang có quá nhiều yêu cầu. Vui lòng chờ một lát rồi thử lại.",
            HttpStatusCode.InternalServerError => "Máy chủ API gặp lỗi. Xem log ở terminal đang chạy API.",
            _ => $"Gọi API thất bại (mã {(int)response.StatusCode})."
        };

    private sealed record ApiProblem(string? Title, string? Detail, int? Status, string? Code, Dictionary<string, string[]>? Errors);
}
