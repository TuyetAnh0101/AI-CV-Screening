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

    public static ApiResult<T> Success(T data) => new() { IsSuccess = true, Data = data };

    public static ApiResult<T> Failure(string message, int? statusCode = null) =>
        new() { IsSuccess = false, ErrorMessage = message, StatusCode = statusCode };

    public static ApiResult<T> Failure(string message, int? statusCode, string? errorCode, TimeSpan? retryAfter = null) =>
        new() { IsSuccess = false, ErrorMessage = message, StatusCode = statusCode, ErrorCode = errorCode, RetryAfter = retryAfter };
}
