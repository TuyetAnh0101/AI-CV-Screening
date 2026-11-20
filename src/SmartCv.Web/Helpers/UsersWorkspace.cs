using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

public enum UserDialogKind { None, Create, Edit, Lock, Unlock, Reset, Credential }

/// <summary>
/// B29: trạng thái trang /users (A30), tách khỏi Users.razor để test không cần trình duyệt.
///
/// Danh sách: lọc / tìm / phân trang ở MÁY CHỦ (GET /api/users), tổng số lấy từ X-Total-Count; gõ tìm có debounce, kết quả
/// của request cũ bị bỏ (số thứ tự + hủy). Số Admin đang hoạt động lấy riêng trên toàn hệ thống (không phụ thuộc trang đang xem)
/// để làm mờ nút khóa / hạ quyền Admin cuối cùng — API vẫn kiểm tra lại (409 admin_safety).
///
/// Ghi: mỗi hộp thoại một lần gửi (Busy chặn bấm trùng), gửi If-Match = Version đã xem. Không bao giờ tự gửi lại; kết quả không
/// rõ (mất mạng, 5xx) → tải lại danh sách (chỉ đọc) và báo người dùng kiểm tra. Mật khẩu tạm chỉ nằm trong bộ nhớ tới khi đóng hộp thoại.
/// </summary>
public sealed class UsersWorkspace(UserApiClient api) : IDisposable
{
    public static readonly int[] PageSizes = [20, 50, 100];
    /// <summary>Thời gian chờ sau lần gõ cuối trước khi tìm (test đặt 0).</summary>
    public TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(350);

    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? loadCts, searchCts;
    private int sequence;
    private bool disposed;

    /// <summary>Gọi khi trạng thái đổi ngoài một lần bấm (debounce xong, tải xong) để trang vẽ lại.</summary>
    public Func<Task>? Changed { get; set; }

    // ---------- Danh sách ----------
    public string SearchText { get; private set; } = "";
    public string? Role { get; private set; }
    public bool? Active { get; private set; }
    public int Page { get; private set; } = 1;
    public int PageSize { get; private set; } = 20;

    public IReadOnlyList<UserDto> Items { get; private set; } = [];
    public int? Total { get; private set; }
    public int? PageCount => Total is { } t ? Math.Max(1, (int)Math.Ceiling(t / (double)PageSize)) : null;
    public int? ActiveAdmins { get; private set; }
    public bool Loading { get; private set; }
    public bool Loaded { get; private set; }
    public string? LoadError { get; private set; }
    public bool HasFilters => !string.IsNullOrWhiteSpace(SearchText) || Role is not null || Active is not null;

    /// <summary>Thông báo kết quả thao tác (thanh trên bảng).</summary>
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }

    public async Task LoadAsync()
    {
        if (disposed) return;
        loadCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loadCts = cts;
        var mine = ++sequence;
        Loading = true;
        LoadError = null;
        try
        {
            var query = new UserQuery(SearchText, Role, Active, Page, PageSize);
            var listTask = api.ListAsync(query, cts.Token);
            var adminsTask = api.CountActiveAdminsAsync(cts.Token);
            var list = await listTask;
            var admins = await adminsTask;
            if (mine != sequence || disposed) return;               // kết quả của request cũ: bỏ
            ActiveAdmins = admins.IsSuccess ? admins.Data : null;
            if (list.IsSuccess)
            {
                var data = list.Data!;
                // Trang vượt quá tổng (vd. vừa khóa người cuối của trang khi lọc "Đang hoạt động"): lùi về trang cuối có dữ liệu.
                if (data.Items.Count == 0 && data.Total is > 0 && Page > 1)
                {
                    Page = data.PageCount ?? 1;
                    Loading = false;
                    await LoadAsync();
                    return;
                }
                (Items, Total, Loaded) = (data.Items, data.Total, true);
            }
            else LoadError = ListError(list);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            if (mine == sequence) Loading = false;
        }
    }

    /// <summary>Ô tìm kiếm: đợi người dùng ngừng gõ rồi mới gửi (debounce); lần gõ mới hủy lần chờ cũ.</summary>
    public async Task SearchAsync(string text)
    {
        SearchText = text;
        searchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        searchCts = cts;
        try { await Task.Delay(SearchDelay, cts.Token); }
        catch (OperationCanceledException) { return; }
        Page = 1;
        await LoadAsync();
        if (Changed is { } changed) await changed();
    }

    public Task FilterRoleAsync(string? role) { Role = UserRoles.IsValid(role) ? role : null; Page = 1; return LoadAsync(); }
    public Task FilterActiveAsync(bool? active) { Active = active; Page = 1; return LoadAsync(); }
    public Task PageSizeAsync(int size) { PageSize = PageSizes.Contains(size) ? size : 20; Page = 1; return LoadAsync(); }
    public Task GoToPageAsync(int page)
    {
        if (page < 1 || (PageCount is { } count && page > count) || page == Page) return Task.CompletedTask;
        Page = page;
        return LoadAsync();
    }

    public Task ClearFiltersAsync()
    {
        searchCts?.Cancel();
        (SearchText, Role, Active, Page) = ("", null, null, 1);
        return LoadAsync();
    }

    public void DismissMessage() => Message = null;

    // ---------- Hộp thoại ----------
    public UserDialogKind Dialog { get; private set; }
    public UserDto? Target { get; private set; }
    public bool Busy { get; private set; }
    public string? DialogError { get; private set; }
    /// <summary>409 account_conflict: dữ liệu trên máy chủ đã đổi — phải tải lại trước khi làm lại.</summary>
    public bool NeedsReload { get; private set; }
    public Dictionary<string, string> FieldErrors { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Form tạo / sửa
    public string FormName { get; set; } = "";
    public string FormEmail { get; set; } = "";
    public string FormRole { get; set; } = UserRoles.Hr;
    public bool GeneratePassword { get; set; } = true;
    public string InitialPassword { get; set; } = "";
    public bool ShowInitialPassword { get; set; }
    public bool ConfirmAdmin { get; set; }

    /// <summary>Mật khẩu tạm vừa tạo / đặt lại: hiện MỘT lần, xóa khi đóng hộp thoại.</summary>
    public string? TemporaryPassword { get; private set; }
    public string? CredentialTitle { get; private set; }

    public bool IsSelf(UserDto u, int? me) => me == u.UserId;

    public void OpenCreate()
    {
        if (Busy) return;
        ResetDialog();
        Dialog = UserDialogKind.Create;
    }

    public void OpenEdit(UserDto u)
    {
        if (Busy) return;
        ResetDialog();
        (Dialog, Target, FormName, FormRole) = (UserDialogKind.Edit, u, u.FullName, u.Role);
    }

    public void OpenLock(UserDto u, int? me)
    {
        if (Busy || UserText.LockBlockedReason(u, me, ActiveAdmins) is not null || !u.IsActive) return;
        ResetDialog();
        (Dialog, Target) = (UserDialogKind.Lock, u);
    }

    public void OpenUnlock(UserDto u)
    {
        if (Busy || u.IsActive) return;
        ResetDialog();
        (Dialog, Target) = (UserDialogKind.Unlock, u);
    }

    public void OpenReset(UserDto u)
    {
        if (Busy || UserText.ResetBlockedReason(u) is not null) return;
        ResetDialog();
        (Dialog, Target) = (UserDialogKind.Reset, u);
    }

    /// <summary>Đóng hộp thoại (không đóng được khi đang gửi). Luôn xóa mật khẩu tạm và mật khẩu ban đầu khỏi bộ nhớ.</summary>
    public bool CloseDialog()
    {
        if (Busy) return false;
        ResetDialog();
        return true;
    }

    private void ResetDialog()
    {
        (Dialog, Target, DialogError, NeedsReload, TemporaryPassword, CredentialTitle) = (UserDialogKind.None, null, null, false, null, null);
        (FormName, FormEmail, FormRole, GeneratePassword, InitialPassword, ShowInitialPassword, ConfirmAdmin) = ("", "", UserRoles.Hr, true, "", false, false);
        FieldErrors.Clear();
    }

    /// <summary>Tạo tài khoản đang mở hộp thoại cần xác nhận cấp quyền Admin (vai trò cao).</summary>
    public bool AdminConfirmationRequired => FormRole == UserRoles.Admin && (Dialog == UserDialogKind.Create || (Dialog == UserDialogKind.Edit && Target?.Role != UserRoles.Admin));

    public bool EditHasChanges => Target is { } t && (CandidateName(FormName) != t.FullName || FormRole != t.Role);

    private static string? CandidateName(string name) => SmartCv.Core.Candidates.CandidateRules.NormalizeName(name);

    public async Task SubmitCreateAsync()
    {
        if (Busy || Dialog != UserDialogKind.Create) return;
        FieldErrors.Clear(); DialogError = null;
        var password = GeneratePassword ? null : InitialPassword;
        var (input, errors) = UserRules.ValidateCreate(FormEmail, FormName, FormRole, password);
        foreach (var (key, value) in errors) FieldErrors[key] = value[0];
        if (!GeneratePassword && string.IsNullOrEmpty(InitialPassword)) FieldErrors["password"] = "Nhập mật khẩu ban đầu, hoặc để hệ thống tự tạo mật khẩu tạm.";
        if (AdminConfirmationRequired && !ConfirmAdmin) FieldErrors["confirmAdmin"] = "Hãy xác nhận cấp quyền Quản trị viên.";
        if (input is null || FieldErrors.Count > 0) return;

        Busy = true;
        ApiResult<UserCredentialResponse> result;
        try { result = await api.CreateAsync(new CreateUserRequest { Email = input.Email, FullName = input.FullName, Role = input.Role, Password = password }, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        finally { Busy = false; InitialPassword = ""; }

        if (result.IsSuccess && result.Data is { User.UserId: > 0 } created)
        {
            Notify($"Đã tạo tài khoản {created.User.FullName} ({UserText.Role(created.User.Role)}). Người dùng phải đổi mật khẩu ở lần đăng nhập đầu tiên.");
            ShowCredential(created, "Tài khoản đã được tạo");
            await LoadAsync();
            return;
        }
        if (result.StatusCode == 409 && result.ErrorCode == "email_conflict") { FieldErrors["email"] = "Email này đã có tài khoản. Tìm tài khoản đó trong danh sách để mở khóa hoặc đặt lại mật khẩu."; return; }
        if (result.StatusCode == 400) { ApplyServerErrors(result); return; }
        if (IsUnknown(result))
        {
            DialogError = "Chưa nhận được xác nhận từ máy chủ — tài khoản CÓ THỂ đã được tạo. SCApp đã tải lại danh sách; hãy tìm theo email trước khi tạo lại. "
                + "Nếu tài khoản đã có, mật khẩu tạm không hiển thị lại được: dùng \"Đặt lại mật khẩu\" (với HR).";
            await LoadAsync();
            return;
        }
        DialogError = WriteError(result);
    }

    public async Task SubmitEditAsync(int? me)
    {
        if (Busy || Dialog != UserDialogKind.Edit || Target is not { } target || NeedsReload) return;
        FieldErrors.Clear(); DialogError = null;
        var (input, errors) = UserRules.ValidateUpdate(target.Email, FormName, FormRole);
        foreach (var (key, value) in errors) FieldErrors[key] = value[0];
        if (UserText.RoleBlockedReason(target, FormRole, me, ActiveAdmins) is { } blocked) FieldErrors["role"] = blocked;
        if (AdminConfirmationRequired && !ConfirmAdmin) FieldErrors["confirmAdmin"] = "Hãy xác nhận cấp quyền Quản trị viên.";
        if (input is null || FieldErrors.Count > 0) return;
        if (!EditHasChanges) { DialogError = "Chưa có thay đổi nào để lưu."; return; }

        Busy = true;
        ApiResult<UserDto> result;
        try { result = await api.UpdateAsync(target, input.FullName, input.Role, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        finally { Busy = false; }

        if (result.IsSuccess)
        {
            var roleChanged = result.Data!.Role != target.Role;
            CloseDialog();
            Notify(roleChanged
                ? $"Đã cập nhật {result.Data.FullName}: vai trò mới là {UserText.Role(result.Data.Role)}. Người này phải đăng nhập lại."
                : $"Đã cập nhật thông tin của {result.Data.FullName}.");
            await LoadAsync();
            return;
        }
        if (result.StatusCode == 400) { ApplyServerErrors(result); return; }
        await HandleWriteFailureAsync(result);
    }

    public Task ConfirmLockAsync() => RunStateChangeAsync(UserDialogKind.Lock, t => api.LockAsync(t, lifetime.Token),
        u => $"Đã khóa tài khoản {u.FullName}. Mọi phiên đăng nhập của tài khoản này đã kết thúc.");

    public Task ConfirmUnlockAsync() => RunStateChangeAsync(UserDialogKind.Unlock, t => api.UnlockAsync(t, lifetime.Token),
        u => $"Đã mở khóa tài khoản {u.FullName}. Mật khẩu và yêu cầu đổi mật khẩu giữ nguyên.");

    private async Task RunStateChangeAsync(UserDialogKind kind, Func<UserDto, Task<ApiResult<UserDto>>> call, Func<UserDto, string> done)
    {
        if (Busy || Dialog != kind || Target is not { } target || NeedsReload) return;
        DialogError = null;
        Busy = true;
        ApiResult<UserDto> result;
        try { result = await call(target); }
        catch (OperationCanceledException) { return; }
        finally { Busy = false; }
        if (result.IsSuccess)
        {
            CloseDialog();
            Notify(done(result.Data!));
            await LoadAsync();
            return;
        }
        await HandleWriteFailureAsync(result);
    }

    public async Task ConfirmResetAsync()
    {
        if (Busy || Dialog != UserDialogKind.Reset || Target is not { } target || NeedsReload) return;
        DialogError = null;
        Busy = true;
        ApiResult<UserCredentialResponse> result;
        try { result = await api.ResetPasswordAsync(target, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        finally { Busy = false; }
        if (result.IsSuccess && result.Data is { } reset)
        {
            Notify($"Đã đặt lại mật khẩu cho {reset.User.FullName}. Phiên đăng nhập cũ của người này đã kết thúc.");
            ShowCredential(reset, "Mật khẩu tạm mới");
            await LoadAsync();
            return;
        }
        await HandleWriteFailureAsync(result);
    }

    /// <summary>Sau 409 account_conflict: tải lại danh sách rồi lấy bản mới nhất của tài khoản đang mở (không ghi đè).</summary>
    public async Task ReloadTargetAsync()
    {
        if (Busy || Target is not { } target) return;
        await LoadAsync();
        var fresh = Items.FirstOrDefault(u => u.UserId == target.UserId);
        if (fresh is null)
        {
            CloseDialog();
            Notify("Tài khoản vừa thay đổi và không còn trong danh sách đang lọc. Hãy xóa bộ lọc để tìm lại.", error: true);
            return;
        }
        var kind = Dialog;
        ResetDialog();
        Target = fresh;
        Dialog = kind switch
        {
            UserDialogKind.Lock when !fresh.IsActive => UserDialogKind.None,
            UserDialogKind.Unlock when fresh.IsActive => UserDialogKind.None,
            _ => kind
        };
        if (Dialog == UserDialogKind.None) { Notify($"Tài khoản {fresh.FullName} đã ở trạng thái {UserText.Status(fresh).ToLowerInvariant()}.", error: false); Target = null; return; }
        if (Dialog == UserDialogKind.Edit) (FormName, FormRole) = (fresh.FullName, fresh.Role);
        DialogError = "Đã tải thông tin mới nhất. Kiểm tra lại rồi thực hiện.";
    }

    private void ShowCredential(UserCredentialResponse response, string title)
    {
        var password = response.TemporaryPassword;
        ResetDialog();
        if (string.IsNullOrEmpty(password)) return;   // Admin tự nhập mật khẩu ban đầu: không có gì để hiện
        (Dialog, Target, TemporaryPassword, CredentialTitle) = (UserDialogKind.Credential, response.User, password, title);
    }

    private async Task HandleWriteFailureAsync<T>(ApiResult<T> result)
    {
        if (result.StatusCode == 409 && result.ErrorCode == "account_conflict")
        {
            NeedsReload = true;
            DialogError = "Tài khoản này vừa được thay đổi (có thể bởi một Admin khác). Thao tác CHƯA được thực hiện. Hãy tải thông tin mới nhất rồi làm lại.";
            return;
        }
        if (result.StatusCode == 404)
        {
            CloseDialogForce();
            Notify("Tài khoản không còn tồn tại.", error: true);
            await LoadAsync();
            return;
        }
        if (IsUnknown(result))
        {
            DialogError = "Chưa nhận được xác nhận từ máy chủ — thao tác CÓ THỂ đã được thực hiện. SCApp đã tải lại danh sách; hãy kiểm tra trước khi làm lại.";
            await LoadAsync();
            return;
        }
        DialogError = WriteError(result);
    }

    private void CloseDialogForce() { Busy = false; ResetDialog(); }

    private void ApplyServerErrors<T>(ApiResult<T> result)
    {
        foreach (var (key, value) in result.ValidationErrors)
            if (value.Length > 0 && key is "email" or "fullName" or "role" or "password") FieldErrors[key] = value[0];
        if (FieldErrors.Count == 0) DialogError = "Thông tin chưa hợp lệ. Kiểm tra lại các ô rồi thử lại.";
    }

    private void Notify(string text, bool error = false) => (Message, MessageIsError) = (text, error);

    private static bool IsUnknown<T>(ApiResult<T> r) =>
        r.FailureKind is ApiFailureKind.Network or ApiFailureKind.Timeout or ApiFailureKind.InvalidData || r.StatusCode is >= 500;

    /// <summary>Câu lỗi cho thao tác ghi, theo mã trạng thái / mã lỗi của A30 — không dùng chữ của exception.</summary>
    public static string WriteError<T>(ApiResult<T> r) => (r.StatusCode, r.ErrorCode) switch
    {
        _ when r.FailureKind == ApiFailureKind.SignedOut => AuthText.SessionExpired,
        (401, _) => AuthText.SessionExpired,
        (403, "password_change_required") => "Bạn cần đổi mật khẩu trước khi quản lý tài khoản.",
        (403, _) => "Tài khoản của bạn hiện không có quyền quản lý tài khoản.",
        (409, "admin_safety") => "Không thực hiện được: SCApp luôn giữ ít nhất một Admin đang hoạt động, và không cho tự khóa hoặc tự đổi vai trò của chính mình.",
        (409, "admin_password_reset_forbidden") => "Không đặt lại mật khẩu cho tài khoản Admin. Admin tự đổi mật khẩu bằng mật khẩu hiện tại.",
        (409, _) => "Dữ liệu tài khoản vừa thay đổi. Hãy tải lại rồi thử lại.",
        (429, _) => $"Bạn đã thực hiện quá nhiều thao tác mật khẩu / tạo tài khoản. Vui lòng đợi {Seconds(r.RetryAfter)} giây rồi thử lại.",
        (413 or 415, _) => "Yêu cầu không hợp lệ. Vui lòng tải lại trang rồi thử lại.",
        _ => "Không thực hiện được thao tác. Vui lòng thử lại."
    };

    public static string ListError<T>(ApiResult<T> r) => (r.StatusCode, r.FailureKind) switch
    {
        (_, ApiFailureKind.Network) => "Không kết nối được máy chủ SCApp. Kiểm tra kết nối rồi thử lại.",
        (_, ApiFailureKind.Timeout) => "Máy chủ phản hồi quá lâu. Vui lòng thử lại.",
        (_, ApiFailureKind.SignedOut) or (401, _) => AuthText.SessionExpired,
        (403, _) => "Tài khoản của bạn hiện không có quyền xem danh sách tài khoản.",
        (400, _) => "Bộ lọc chưa hợp lệ. Hãy xóa bộ lọc rồi thử lại.",
        (503, _) => "Dịch vụ tài khoản tạm không khả dụng. Vui lòng thử lại sau ít phút.",
        _ => "Chưa tải được danh sách tài khoản. Vui lòng thử lại."
    };

    private static int Seconds(TimeSpan? wait) => wait is { } w && w > TimeSpan.Zero ? (int)Math.Ceiling(w.TotalSeconds) : 60;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        TemporaryPassword = null;
        InitialPassword = "";
    }
}
