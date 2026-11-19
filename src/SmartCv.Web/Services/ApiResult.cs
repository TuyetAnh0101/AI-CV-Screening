namespace SmartCv.Web.Services;

/// <summary>
/// Kết quả một lần gọi API. Trang .razor không cần try/catch:
/// chỉ kiểm tra IsSuccess, đúng thì dùng Data, sai thì hiện ErrorMessage.
/// </summary>
public sealed class ApiResult<T>
{
    public bool IsSuccess { get; private init; }
    public T? Data { get; private init; }
    public string? ErrorMessage { get; private init; }
    public int? StatusCode { get; private init; }

    /// <summary>B39: mã máy đọc được trong ProblemDetails (trường "code", ví dụ PreviewQuotaExhausted); null nếu API không gửi.</summary>
    public string? ErrorCode { get; private init; }

    /// <summary>B39: header Retry-After của API (429/503); null nếu không có.</summary>
    public TimeSpan? RetryAfter { get; private init; }
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; private init; } = new Dictionary<string, string[]>();

    /// <summary>
    /// B27: lỗi xảy ra ở đâu (API trả lỗi, mất kết nối, quá thời gian chờ, chưa đăng nhập). Dùng để chọn câu thông báo,
    /// không phải dựa vào chữ tiếng Việt trong <see cref="ErrorMessage"/>.
    /// </summary>
    public ApiFailureKind FailureKind { get; private init; }

    public static ApiResult<T> Success(T data) => new() { IsSuccess = true, Data = data };

    public static ApiResult<T> Failure(string message, int? statusCode = null) =>
        new() { IsSuccess = false, ErrorMessage = message, StatusCode = statusCode, FailureKind = statusCode is null ? ApiFailureKind.None : ApiFailureKind.Http };

    public static ApiResult<T> Failure(string message, int? statusCode, string? errorCode, TimeSpan? retryAfter = null, IReadOnlyDictionary<string, string[]>? validationErrors = null) =>
        new() { IsSuccess = false, ErrorMessage = message, StatusCode = statusCode, ErrorCode = errorCode, RetryAfter = retryAfter, ValidationErrors = validationErrors ?? new Dictionary<string, string[]>(), FailureKind = statusCode is null ? ApiFailureKind.None : ApiFailureKind.Http };

    /// <summary>B27: lỗi phát sinh ở phía Web — không có phản hồi HTTP, hoặc request không được gửi vì tab chưa đăng nhập.</summary>
    public static ApiResult<T> LocalFailure(string message, ApiFailureKind kind, int? statusCode = null, string? errorCode = null) =>
        new() { IsSuccess = false, ErrorMessage = message, FailureKind = kind, StatusCode = statusCode, ErrorCode = errorCode };
}

/// <summary>B27: nguồn gốc của một lần gọi API thất bại.</summary>
public enum ApiFailureKind
{
    /// <summary>Thành công, hoặc lỗi không phân loại.</summary>
    None,
    /// <summary>API đã trả phản hồi lỗi (xem StatusCode / ErrorCode).</summary>
    Http,
    /// <summary>Không kết nối được máy chủ API.</summary>
    Network,
    /// <summary>API không trả lời trong thời gian chờ.</summary>
    Timeout,
    /// <summary>Phản hồi không đúng định dạng JSON mong đợi.</summary>
    InvalidData,
    /// <summary>Request không được gửi: tab này chưa đăng nhập hoặc phiên đã hết hạn.</summary>
    SignedOut
}
