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

    public static ApiResult<T> Success(T data) => new() { IsSuccess = true, Data = data };

    public static ApiResult<T> Failure(string message, int? statusCode = null) =>
        new() { IsSuccess = false, ErrorMessage = message, StatusCode = statusCode };
}
