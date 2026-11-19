using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace SmartCv.Web.Services;

/// <summary>
/// Lớp cha của mọi service gọi API. Gom chung việc gửi request và đổi lỗi kỹ thuật thành câu tiếng Việt dễ hiểu,
/// để các service con (JobApiClient, CvApiClient...) chỉ còn 1–2 dòng mỗi hàm.
///
/// B27 — xác thực: mỗi lần gọi tạo một <see cref="HttpRequestMessage"/> riêng và gắn "Authorization: Bearer" của
/// <see cref="AuthSession"/> thuộc ĐÚNG circuit đang gọi (session được gắn khi DI tạo client trong scope của circuit,
/// xem ServiceCollectionExtensions). Không dùng DefaultRequestHeaders, không dùng DelegatingHandler (handler của
/// HttpClientFactory sống trong scope DI riêng, có thể bị dùng chung giữa các circuit).
/// - Tab chưa đăng nhập / token quá hạn: KHÔNG gửi request, trả lỗi cục bộ (FailureKind = SignedOut) → không có 401 hàng loạt.
/// - API trả 401: kết thúc phiên đúng một lần (AuthSession.Expired), không gửi lại.
/// - API trả 403: giữ phiên (không đủ quyền khác với chưa đăng nhập); riêng "password_change_required" cập nhật trạng thái.
/// Client không được gắn session (HealthApiClient, AuthApiClient, client do test tự tạo) gửi request như trước, không có token.
/// </summary>
public abstract class ApiClientBase(HttpClient http, ILogger logger)
{
    internal const string SignedOutMessage = "Bạn cần đăng nhập để xem dữ liệu này.";
    internal const string AuthenticationRequiredCode = "authentication_required";
    internal const string PasswordChangeRequiredCode = "password_change_required";

    private AuthSession? _session;

    protected HttpClient Http { get; } = http;

    /// <summary>Chỉ ServiceCollectionExtensions gọi, ngay khi client được tạo trong scope của circuit.</summary>
    internal void UseSession(AuthSession session) => _session = session;

    protected Task<ApiResult<T>> GetAsync<T>(string url, CancellationToken ct = default) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, url), url, ct);

    // B23: search queries and exception details may contain applicant information.
    protected Task<ApiResult<T>> GetPrivateAsync<T>(string url, string route, CancellationToken ct = default) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, url), route, ct, sensitive: true);

    protected Task<ApiResult<T>> PutPrivateJsonAsync<T>(string url, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => Json(HttpMethod.Put, url, body), "CV candidate attachment", ct, sensitive: true);

    // Nội dung do nơi gọi tạo (multipart tải CV, stream từng file): chỉ gắn vào request, không đọc trước, không gửi lại.
    protected Task<ApiResult<T>> PostAsync<T>(string url, HttpContent content, CancellationToken ct = default) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = content }, url, ct);

    protected Task<ApiResult<T>> PostJsonAsync<T>(string url, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => Json(HttpMethod.Post, url, body), url, ct);

    protected Task<ApiResult<T>> PutJsonAsync<T>(string url, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => Json(HttpMethod.Put, url, body), url, ct);

    protected Task<ApiResult<T>> PatchJsonAsync<T>(string url, object body, CancellationToken ct = default) =>
        SendAsync<T>(() => Json(HttpMethod.Patch, url, body), url, ct);

    // B21: DELETE 204 has no JSON body. Share the existing cancellation/error handling.
    protected Task<ApiResult<bool>> DeleteAsync(string url, CancellationToken ct = default) =>
        SendAsync<bool>(() => new HttpRequestMessage(HttpMethod.Delete, url), url, ct, delete: true);

    // SCApp v2: DELETE trả về JSON (ví dụ số thành viên còn lại sau khi bỏ hồ sơ khỏi bộ).
    protected Task<ApiResult<T>> DeleteJsonAsync<T>(string url, CancellationToken ct = default) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Delete, url), url, ct);

    /// <summary>B27: GET với một token cụ thể (AuthSession xác minh phiên đã lưu qua /api/auth/me trước khi nhận phiên).</summary>
    protected Task<ApiResult<T>> GetWithBearerAsync<T>(string url, string token, CancellationToken ct = default) =>
        SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, url), url, ct, bearer: token);

    // Giống PostAsJsonAsync/PutAsJsonAsync: JsonContent mặc định dùng JsonSerializerDefaults.Web.
    private static HttpRequestMessage Json(HttpMethod method, string url, object body) =>
        new(method, url) { Content = JsonContent.Create(body) };

    /// <summary>
    /// B27: gửi một request đã dựng sẵn qua đúng phiên đăng nhập. Trả null (không gửi gì) khi client gắn phiên mà tab chưa
    /// đăng nhập. Dùng cho service con cần tự dựng request (đọc header, ghi có kiểm tra lỗi riêng).
    /// </summary>
    protected async Task<HttpResponseMessage?> SendAuthorizedAsync(HttpRequestMessage request, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, string? bearer = null)
    {
        var token = bearer;
        if (token is null && _session is { } session)
        {
            token = await session.GetAccessTokenAsync();
            if (token is null) return null;
        }
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await Http.SendAsync(request, completion, ct);
        if (_session is { } current && bearer is null && token is not null)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                await current.HandleUnauthorizedAsync(token);
            else if (response.StatusCode == HttpStatusCode.Forbidden && await ReadCodeAsync(response, ct) == PasswordChangeRequiredCode)
                current.HandlePasswordChangeRequired(token);
        }
        return response;
    }

    /// <summary>
    /// Đọc "code" của ProblemDetails mà không làm mất nội dung cho người đọc sau: đệm nội dung rồi đọc thành chuỗi từ bộ đệm
    /// (ReadAsStream sẽ trả cùng một stream cho lần đọc sau, đã ở cuối).
    /// </summary>
    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return System.Text.Json.JsonSerializer.Deserialize<ApiProblem>(text, System.Text.Json.JsonSerializerOptions.Web)?.Code;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or HttpRequestException)
        {
            return null;
        }
    }

    private async Task<ApiResult<T>> SendAsync<T>(Func<HttpRequestMessage> create, string url, CancellationToken ct, bool delete = false, bool sensitive = false, string? bearer = null)
    {
        try
        {
            using var request = create();
            using var response = await SendAuthorizedAsync(request, ct, bearer: bearer);
            if (response is null)
                return ApiResult<T>.LocalFailure(SignedOutMessage, ApiFailureKind.SignedOut, 401, AuthenticationRequiredCode);

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
            return ApiResult<T>.LocalFailure("Không kết nối được máy chủ API. Hãy kiểm tra API đã chạy và ApiBaseUrl đúng cổng.", ApiFailureKind.Network);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Gọi API {Url} quá thời gian chờ", url);
            return ApiResult<T>.LocalFailure("Máy chủ API phản hồi quá lâu. Vui lòng thử lại.", ApiFailureKind.Timeout);
        }
        catch (System.Text.Json.JsonException ex)
        {
            if (sensitive) logger.LogError("Dữ liệu từ {Route} không đúng định dạng", url);
            else logger.LogError(ex, "Dữ liệu từ {Url} không đúng định dạng", url);
            return ApiResult<T>.LocalFailure("Dữ liệu API trả về không đúng định dạng mong đợi.", ApiFailureKind.InvalidData);
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
