using System.Globalization;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>Bộ lọc danh sách tài khoản (GET /api/users). Null = không lọc.</summary>
public sealed record UserQuery(string? Search = null, string? Role = null, bool? IsActive = null, int Page = 1, int PageSize = 20)
{
    /// <summary>Đường dẫn có query; chỉ gửi tham số đang dùng.</summary>
    public string ToUrl()
    {
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(Search)) q.Add("search=" + Uri.EscapeDataString(Search.Trim()));
        if (!string.IsNullOrEmpty(Role)) q.Add("role=" + Uri.EscapeDataString(Role));
        if (IsActive is { } active) q.Add("isActive=" + (active ? "true" : "false"));
        q.Add("page=" + Page.ToString(CultureInfo.InvariantCulture));
        q.Add("pageSize=" + PageSize.ToString(CultureInfo.InvariantCulture));
        return "api/users?" + string.Join("&", q);
    }
}

/// <summary>Một trang tài khoản. <see cref="Total"/> lấy từ header X-Total-Count của API — null khi API không gửi (không đoán).</summary>
public sealed record UserPage(IReadOnlyList<UserDto> Items, int? Total, int Page, int PageSize)
{
    public int? PageCount => Total is { } t ? Math.Max(1, (int)Math.Ceiling(t / (double)PageSize)) : null;
}

/// <summary>
/// B29: gọi API quản lý tài khoản (A30, chỉ Admin). Gắn phiên đăng nhập như mọi client nghiệp vụ (B27).
/// - Mọi thao tác ghi trên một tài khoản gửi If-Match = Version đã xem: hai Admin sửa cùng lúc → 409 account_conflict, không ghi đè.
/// - Không tự gửi lại thao tác ghi (tạo, khóa, đặt lại mật khẩu) khi lỗi mạng: kết quả có thể đã được ghi.
/// - Log chỉ ghi nhãn đường dẫn, không ghi từ khóa tìm kiếm (email), không ghi nội dung (mật khẩu tạm).
/// </summary>
public sealed class UserApiClient(HttpClient http, ILogger<UserApiClient> logger) : ApiClientBase(http, logger)
{
    public async Task<ApiResult<UserPage>> ListAsync(UserQuery query, CancellationToken ct = default)
    {
        int? total = null; int page = query.Page, size = query.PageSize;
        var result = await SendRequestAsync<List<UserDto>>(() => new HttpRequestMessage(HttpMethod.Get, query.ToUrl()), "api/users", ct, response =>
        {
            total = Header(response, "X-Total-Count");
            page = Header(response, "X-Page") ?? page;
            size = Header(response, "X-Page-Size") ?? size;
        });
        return result.IsSuccess
            ? ApiResult<UserPage>.Success(new UserPage(result.Data!, total, page, size))
            : Forward<List<UserDto>, UserPage>(result);
    }

    /// <summary>Số Admin đang hoạt động trên TOÀN hệ thống (không phụ thuộc trang / bộ lọc đang xem).</summary>
    public async Task<ApiResult<int>> CountActiveAdminsAsync(CancellationToken ct = default)
    {
        var result = await ListAsync(new UserQuery(Role: SmartCv.Core.Auth.UserRoles.Admin, IsActive: true, PageSize: 1), ct);
        return result.IsSuccess && result.Data!.Total is { } total
            ? ApiResult<int>.Success(total)
            : result.IsSuccess ? ApiResult<int>.Failure("API không trả tổng số tài khoản.", null) : Forward<UserPage, int>(result);
    }

    /// <summary>POST /api/users. Password null → API tạo mật khẩu tạm và trả về MỘT lần. 409 email_conflict.</summary>
    public Task<ApiResult<UserCredentialResponse>> CreateAsync(CreateUserRequest request, CancellationToken ct = default) =>
        SendRequestAsync<UserCredentialResponse>(() => JsonRequest(HttpMethod.Post, "api/users", request), "api/users (create)", ct);

    /// <summary>PUT /api/users/{id}: họ tên, vai trò. Email không đổi được.</summary>
    public Task<ApiResult<UserDto>> UpdateAsync(UserDto target, string fullName, string role, CancellationToken ct = default) =>
        SendRequestAsync<UserDto>(() => IfMatch(JsonRequest(HttpMethod.Put, $"api/users/{target.UserId}",
            new UpdateUserRequest { ExpectedVersion = target.Version, FullName = fullName, Role = role }), target), "api/users/{id} (update)", ct);

    public Task<ApiResult<UserDto>> LockAsync(UserDto target, CancellationToken ct = default) => Post<UserDto>(target, "lock", ct);

    public Task<ApiResult<UserDto>> UnlockAsync(UserDto target, CancellationToken ct = default) => Post<UserDto>(target, "unlock", ct);

    /// <summary>Chỉ cho tài khoản HR (API trả 409 admin_password_reset_forbidden với Admin).</summary>
    public Task<ApiResult<UserCredentialResponse>> ResetPasswordAsync(UserDto target, CancellationToken ct = default) =>
        Post<UserCredentialResponse>(target, "reset-password", ct);

    private Task<ApiResult<T>> Post<T>(UserDto target, string action, CancellationToken ct) =>
        SendRequestAsync<T>(() => IfMatch(new HttpRequestMessage(HttpMethod.Post, $"api/users/{target.UserId}/{action}"), target), $"api/users/{{id}}/{action}", ct);

    private static HttpRequestMessage IfMatch(HttpRequestMessage request, UserDto target)
    {
        if (!string.IsNullOrEmpty(target.Version)) request.Headers.TryAddWithoutValidation("If-Match", '"' + target.Version + '"');
        return request;
    }

    private static int? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static ApiResult<TOut> Forward<TIn, TOut>(ApiResult<TIn> failed) => failed.FailureKind is ApiFailureKind.Http or ApiFailureKind.None
        ? ApiResult<TOut>.Failure(failed.ErrorMessage ?? "Không tải được dữ liệu.", failed.StatusCode, failed.ErrorCode, failed.RetryAfter, failed.ValidationErrors)
        : ApiResult<TOut>.LocalFailure(failed.ErrorMessage ?? "Không tải được dữ liệu.", failed.FailureKind, failed.StatusCode, failed.ErrorCode);
}
