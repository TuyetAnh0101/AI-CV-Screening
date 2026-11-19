using Microsoft.AspNetCore.Components.Web;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Evaluation;

namespace SmartCv.Web.Components.Results;

/// <summary>
/// SCApp v2: cờ HR thủ công, menu thao tác từng dòng và panel "Xem nhanh".
/// Mở menu / mở hộp thoại KHÔNG lưu gì; chỉ nút xác nhận trong hộp thoại mới gọi API.
/// Gắn hoặc xử lý cờ không đổi giai đoạn; ghi xong luôn đọc lại bảng từ API (không tự gửi lại khi lỗi).
/// </summary>
public partial class RankingTable
{
    private bool manualOnly;
    /// <summary>Kết quả ghi cờ vừa xác nhận, dùng tới khi bảng được tải lại. null = đã xử lý (không còn cờ mở).</summary>
    private readonly Dictionary<int, ManualFlagDto?> manualOverrides = [];
    private int? openMenu;
    private RankingItemDto? drawerRow;
    private int historyVersion;
    private ManualFlagDialog.FlagRequest? flagRequest;
    private int flagRowId;
    private string? flagError;

    private ManualFlagDto? ManualFlagOf(RankingItemDto row) =>
        manualOverrides.TryGetValue(row.ApplicationId, out var value) ? value : row.ManualFlag is { Status: ManualFlagStatuses.Open } open ? open : null;

    /// <summary>Cùng định nghĩa với API và Tổng quan, tính theo giai đoạn HIỆN TẠI (kể cả quyết định vừa lưu).</summary>
    private bool NeedsReviewOf(RankingItemDto row) => ReviewAttention.NeedsReview(StageOf(row), row.ReviewFlags.Count > 0, ManualFlagOf(row) is not null);
    private int NeedsReviewCount => Items.Count(NeedsReviewOf);
    private int ManualCount => Items.Count(r => ManualFlagOf(r) is not null);

    private void ToggleManualOnly() { manualOnly = !manualOnly; selected.Clear(); }
    private void ToggleMenu(int applicationId) => openMenu = openMenu == applicationId ? null : applicationId;
    private void MenuKey(KeyboardEventArgs e) { if (e.Key == "Escape") openMenu = null; }

    private void OpenDrawer(RankingItemDto row) { openMenu = null; drawerRow = row; }
    private void CloseDrawer() { if (flagRequest is null) drawerRow = null; }

    private async Task OpenFlagAsync(RankingItemDto row, ManualFlagDialog.FlagRequest request)
    {
        openMenu = null;
        if (TableLocked) return;
        if (request.Mode != ManualFlagDialog.FlagMode.Create && request.Flag is null) return;
        flagRowId = row.ApplicationId; flagRequest = request; flagError = null;
        await OnInteractionChanged.InvokeAsync(true);   // tạm dừng cập nhật tự động trong lúc HR thao tác
    }

    private async Task CancelFlag()
    {
        if (busy) return;
        flagRequest = null; flagError = null;
        await OnInteractionChanged.InvokeAsync(false);
    }

    private async Task ConfirmFlagAsync(ManualFlagDialog.FlagDecision decision)
    {
        if (flagRequest is not { } request || busy || disposed) return;
        busy = true; flagError = null; message = null;
        var appId = flagRowId;
        try
        {
            await ActorState.SetAsync(decision.Actor);
            var result = request.Mode switch
            {
                ManualFlagDialog.FlagMode.Create => await ApplicationApi.CreateFlagAsync(appId, new() { Reason = decision.Reason!, Note = decision.Note, Actor = decision.Actor }, lifetime.Token),
                ManualFlagDialog.FlagMode.Resolve => await ApplicationApi.ResolveFlagAsync(request.Flag!.FlagId, new() { Actor = decision.Actor, Note = decision.Note, ExpectedVersion = request.Flag.Version }, lifetime.Token),
                _ => await ApplicationApi.ReopenFlagAsync(request.Flag!.FlagId, new() { Actor = decision.Actor, Note = decision.Note, ExpectedVersion = request.Flag.Version }, lifetime.Token)
            };
            if (disposed) return;
            if (result.IsSuccess)
            {
                var flag = result.Data!;
                manualOverrides[appId] = flag.Status == ManualFlagStatuses.Open ? flag : null;
                Show(request.Mode switch
                {
                    ManualFlagDialog.FlagMode.Create => $"Đã đánh dấu hồ sơ #{appId} cần kiểm tra ({flag.ReasonName}). Giai đoạn không thay đổi.",
                    ManualFlagDialog.FlagMode.Resolve => $"Đã đánh dấu cờ của hồ sơ #{appId} là đã xử lý.",
                    _ => $"Đã mở lại cờ kiểm tra của hồ sơ #{appId}."
                }, false);
                flagRequest = null;
            }
            else
            {
                // 409: HR khác vừa thao tác. Không tự gửi lại; giữ hộp thoại, đọc lại bảng để HR thấy trạng thái mới.
                flagError = result.StatusCode switch
                {
                    409 => (Anonymity.Enabled ? "Cờ vừa được HR khác thay đổi." : result.ErrorMessage) + " Bảng đã được tải lại; hãy đóng hộp thoại và kiểm tra trạng thái mới.",
                    null => "Chưa xác nhận được kết quả lưu. Hệ thống sẽ tải lại; không tự gửi lại.",
                    _ => result.ErrorMessage ?? "Không lưu được cờ."
                };
            }
            historyVersion++;
            needsSync = !await SynchronizeAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            busy = false;
            if (!disposed && flagRequest is null) await OnInteractionChanged.InvokeAsync(false);
        }
    }
}
