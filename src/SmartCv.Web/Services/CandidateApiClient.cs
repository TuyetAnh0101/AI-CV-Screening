using System.Net.Http.Json;
using System.Text.Json;
using SmartCv.Core.Contracts;
using SmartCv.Core.Cvs;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Services;

/// <summary>
/// Gọi API ứng viên (A24/A43). Mỗi thao tác gọi đúng endpoint sở hữu dữ liệu; không có endpoint gộp CV + ứng viên.
///   B23: tìm người đã có để gắn CV (<see cref="ListAsync"/>).
///   B24: danh sách người, chi tiết, tạo, sửa, gộp, xuất / xóa dữ liệu, nhật ký yêu cầu.
/// Lỗi trả về là chữ cố định hoặc lỗi kiểm tra theo trường; không bao giờ hiện thông báo thô của máy chủ (có thể chứa thông tin cá nhân).
/// Không tự gửi lại thao tác ghi.
/// </summary>
public sealed class CandidateApiClient(HttpClient http, ILogger<CandidateApiClient> logger) : ApiClientBase(http, logger)
{
    private readonly ILogger<CandidateApiClient> log = logger;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>B23: tìm người đã có trong hộp thoại gắn CV (2–100 ký tự, 10 người / trang).</summary>
    public Task<ApiResult<CandidatePageDto>> ListAsync(string search, int page = 1, CancellationToken ct = default)
    {
        var normalized = CvListQuery.NormalizeSearch(search);
        if (normalized is null || normalized.Length is < 2 or > 100 || page < 1)
            return Task.FromResult(ApiResult<CandidatePageDto>.Failure("Từ khóa cần từ 2 đến 100 ký tự.", 400));
        return GetPrivateAsync<CandidatePageDto>($"api/candidates?search={Uri.EscapeDataString(normalized)}&page={page}&pageSize=10", "Candidate search", ct);
    }

    /// <summary>B24: GET /api/candidates?search=&amp;page=&amp;pageSize= — danh sách NGƯỜI (không phải file CV), phân trang ở máy chủ.</summary>
    public Task<ApiResult<CandidatePageDto>> ListPeopleAsync(CandidateListFilter filter, CancellationToken ct = default)
    {
        var (valid, error) = filter.Validate();
        return valid is null ? Task.FromResult(ApiResult<CandidatePageDto>.Failure(error!, 400))
            : GetPrivateAsync<CandidatePageDto>("api/candidates?" + valid.ApiQuery, "Candidate list", ct);
    }

    /// <summary>GET /api/candidates/{id}: liên hệ, CV, hồ sơ ứng tuyển + dòng thời gian, người có thể trùng, nhật ký yêu cầu.</summary>
    public Task<ApiResult<CandidateDetailDto>> GetAsync(int id, CancellationToken ct = default) =>
        GetPrivateAsync<CandidateDetailDto>($"api/candidates/{id}", "Candidate detail", ct);

    /// <summary>POST /api/candidates. 409 = có thể trùng người đã có (kèm Matches); chỉ gửi AllowDuplicate=true sau khi HR quyết định.</summary>
    public Task<CandidateWriteResult<CandidateDto>> CreateAsync(CandidateRequest request, CancellationToken ct = default) =>
        WriteAsync<CandidateDto>(HttpMethod.Post, "api/candidates", request, "Candidate create", ct);

    /// <summary>PUT /api/candidates/{id}. 409 = email / số điện thoại trùng người khác (kèm Matches).</summary>
    public Task<CandidateWriteResult<CandidateDto>> UpdateAsync(int id, CandidateRequest request, CancellationToken ct = default) =>
        WriteAsync<CandidateDto>(HttpMethod.Put, $"api/candidates/{id}", request, "Candidate update", ct);

    /// <summary>POST /api/candidates/{sourceId}/merge: chuyển mọi CV của sourceId sang targetId rồi xóa sourceId. Không hoàn tác.</summary>
    public Task<CandidateWriteResult<MergeCandidateResultDto>> MergeAsync(int sourceId, int targetId, string actor, CancellationToken ct = default) =>
        WriteAsync<MergeCandidateResultDto>(HttpMethod.Post, $"api/candidates/{sourceId}/merge",
            new MergeCandidateRequest { TargetCandidateId = targetId, Actor = actor }, "Candidate merge", ct);

    /// <summary>POST /api/candidates/{id}/erase (A43): xóa dữ liệu theo yêu cầu ứng viên. Confirm luôn true vì giao diện đã xác nhận 2 bước.</summary>
    public Task<CandidateWriteResult<EraseCandidateResultDto>> EraseAsync(int id, string handledBy, string? note, CancellationToken ct = default) =>
        WriteAsync<EraseCandidateResultDto>(HttpMethod.Post, $"api/candidates/{id}/erase",
            new EraseCandidateRequest { Confirm = true, HandledBy = handledBy, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() }, "Candidate erase", ct);

    /// <summary>GET /api/data-requests?candidateId= — nhật ký yêu cầu, còn giữ sau khi ứng viên đã bị xóa.</summary>
    public Task<ApiResult<List<DataRequestDto>>> ListRequestsAsync(int candidateId, CancellationToken ct = default) =>
        GetPrivateAsync<List<DataRequestDto>>($"api/data-requests?candidateId={candidateId}", "Candidate data requests", ct);

    /// <summary>A43/B43: stream JSON qua đúng phiên. Backend lấy người xuất từ JWT, không có handledBy trong URL.</summary>
    public Task<ApiResult<DownloadedFile>> ExportAsync(int id, CancellationToken ct = default) =>
        GetFileAsync($"api/candidates/{id}/data-export", ct);

    private async Task<CandidateWriteResult<T>> WriteAsync<T>(HttpMethod method, string url, object body, string route, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, options: Json) };
            // B27: gắn Bearer của phiên hiện tại; tab chưa đăng nhập thì không gửi thao tác ghi.
            using var response = await SendAuthorizedAsync(request, ct);
            if (response is null) return CandidateWriteResult<T>.Fail(401, CandidateText.Error(401, writing: true));
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<T>(Json, ct);
                return data is null ? CandidateWriteResult<T>.Fail(status, CandidateText.Error(status, writing: true)) : CandidateWriteResult<T>.Ok(data, status);
            }
            var (errors, matches) = await ReadProblemAsync(response, ct);
            log.LogWarning("Gọi API {Route} lỗi {Status}", route, status);
            return CandidateWriteResult<T>.Fail(status, CandidateText.Error(status, writing: true), errors, matches);
        }
        catch (HttpRequestException)
        {
            log.LogError("Không kết nối được API khi gọi {Route}", route);
            return CandidateWriteResult<T>.Fail(null, CandidateText.Error(null, writing: true));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogError("Gọi API {Route} quá thời gian chờ", route);
            return CandidateWriteResult<T>.Fail(null, CandidateText.Error(null, writing: true));
        }
        catch (JsonException)
        {
            log.LogError("Dữ liệu từ {Route} không đúng định dạng", route);
            return CandidateWriteResult<T>.Fail(null, CandidateText.Error(null, writing: true));
        }
    }

    /// <summary>Chỉ đọc lỗi theo trường (thông điệp quy tắc của Core) và danh sách người có thể trùng; bỏ qua title/detail tự do.</summary>
    private static async Task<(Dictionary<string, string[]> Errors, List<DuplicateMatchDto> Matches)> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<CandidateProblem>(Json, ct);
            return (problem?.Errors ?? [], problem?.Matches ?? []);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { return ([], []); }
    }

    private sealed record CandidateProblem(Dictionary<string, string[]>? Errors, List<DuplicateMatchDto>? Matches);
}

/// <summary>Kết quả ghi của API ứng viên. StatusCode null = không xác định được (mất kết nối / quá thời gian): KHÔNG tự gửi lại.</summary>
public sealed class CandidateWriteResult<T>
{
    public bool IsSuccess { get; private init; }
    public T? Data { get; private init; }
    public int? StatusCode { get; private init; }
    public string? ErrorMessage { get; private init; }
    public IReadOnlyDictionary<string, string[]> Errors { get; private init; } = new Dictionary<string, string[]>();
    public IReadOnlyList<DuplicateMatchDto> Matches { get; private init; } = [];
    /// <summary>Kết quả ghi chưa xác định: có thể máy chủ đã lưu. Phải tải lại để kiểm tra trước khi thao tác tiếp.</summary>
    public bool Uncertain => !IsSuccess && StatusCode is null or >= 500;
    public static CandidateWriteResult<T> Ok(T data, int status) => new() { IsSuccess = true, Data = data, StatusCode = status };
    public static CandidateWriteResult<T> Fail(int? status, string message, Dictionary<string, string[]>? errors = null, List<DuplicateMatchDto>? matches = null) =>
        new() { StatusCode = status, ErrorMessage = message, Errors = errors ?? [], Matches = matches ?? [] };
}
