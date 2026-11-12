using Microsoft.AspNetCore.Components;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Components.Results;

public partial class RankingTable
{
    [Parameter] public Func<Task<bool>>? ReloadStages { get; set; }
    [Parameter] public EventCallback<bool> OnInteractionChanged { get; set; }
    private sealed record Snapshot(int Id, string Stage, Guid Version);
    private sealed record PendingDecision(string ToStage, List<Snapshot> Rows, bool Single);
    private readonly HashSet<int> selected = [];
    private readonly Dictionary<int, StageChangeResultDto> overrides = [];
    private readonly CancellationTokenSource lifetime = new();
    private List<RankingItemDto>? lastItems;
    private PendingDecision? pending;
    private string? stageFilter, message;
    private bool messageIsError, busy, actorReady, needsSync, editingActor, disposed, showDecisionDialog;
    private int selectVersion;
    private bool TableLocked => Loading || busy || !actorReady || needsSync || pending is not null || editingActor;
    private string StageOf(RankingItemDto row) => overrides.GetValueOrDefault(row.ApplicationId)?.Stage ?? row.Stage;
    private string? ReasonOf(RankingItemDto row) => overrides.TryGetValue(row.ApplicationId, out var value) ? value.RejectReason : row.RejectReason;
    private Guid VersionOf(RankingItemDto row) => overrides.GetValueOrDefault(row.ApplicationId)?.StageVersion ?? row.StageVersion;
    private bool flaggedOnly;
    [Parameter] public int FlaggedCount { get; set; }
    private IEnumerable<RankingItemDto> FlagFilteredItems => Items.Where(r => !flaggedOnly || r.ReviewFlags.Count > 0);
    private IEnumerable<RankingItemDto> StageFilteredItems => FlagFilteredItems.Where(r => stageFilter is null || StageOf(r) == stageFilter);
    private List<RankingItemDto> VisibleRows => Apply(StageFilteredItems, filter);
    private List<RankingItemDto> Apply(IEnumerable<RankingItemDto> rows, RankingFilter fit) => Anonymity.Sort(rows.Where(r => Anonymity.Matches(r, search) && RankingView.MatchesFilter(r, fit)), sort).ToList();
    private bool AllVisibleSelected => VisibleRows.Count > 0 && VisibleRows.All(r => selected.Contains(r.ApplicationId));
    private bool SomeVisibleSelected => !AllVisibleSelected && VisibleRows.Any(r => selected.Contains(r.ApplicationId));
    private int StageCount(string? stage) => Apply(FlagFilteredItems, filter).Count(r => stage is null || StageOf(r) == stage);
    private string DialogTitle => pending!.Single
        ? $"{StageText.Name(pending.ToStage)} · {NameOf(Items.First(r => r.ApplicationId == pending.Rows[0].Id))}"
        : $"{StageText.Name(pending.ToStage)} · {pending.Rows.Count} hồ sơ";
    private string? DialogReason => pending is { Single: true, ToStage: ApplicationStages.Rejected }
        && Items.FirstOrDefault(r => r.ApplicationId == pending.Rows[0].Id) is { } row ? ReasonOf(row) : null;

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(lastItems, Items)) return;
        lastItems = Items;
        overrides.Clear();
        needsSync = false; // A successful manual refresh or poll is authoritative as well.
        selected.IntersectWith(VisibleRows.Select(r => r.ApplicationId));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !RendererInfo.IsInteractive) return;
        await ActorState.EnsureLoadedAsync();
        if (disposed) return;
        actorReady = true;
        StateHasChanged();
    }

    private void ClearSelection() => selected.Clear();
    protected override void OnInitialized() => Anonymity.Changed += PrivacyChanged;
    private string NameOf(RankingItemDto row) => Anonymity.Name(row.CandidateName, row.Rank, row.ApplicationId);
    private void PrivacyChanged() => _ = InvokeAsync(() => { search = ""; message = null; if (Anonymity.Enabled && sort == RankingSort.NameAsc) sort = RankingSort.FinalDesc; selected.Clear(); StateHasChanged(); });
    private void SetFitFilter(RankingFilter value) { filter = value; selected.Clear(); }
    private void SetStageFilter(string? stage) { stageFilter = stage; selected.Clear(); }
    private void Toggle(int id, ChangeEventArgs e)
    {
        if (TableLocked) return;
        if (e.Value is not true) { selected.Remove(id); return; }
        if (selected.Count >= 200) { Show("Mỗi lần chỉ được chọn tối đa 200 hồ sơ. Hãy chia thành nhiều lượt.", true); selectVersion++; return; }
        selected.Add(id);
    }
    private void ToggleAll(ChangeEventArgs e)
    {
        if (TableLocked) return;
        var rows = VisibleRows;
        if (rows.Count > 200) { Show("Hãy lọc còn tối đa 200 hồ sơ trước khi chọn tất cả.", true); return; }
        if (e.Value is true) selected.UnionWith(rows.Select(r => r.ApplicationId));
        else selected.ExceptWith(rows.Select(r => r.ApplicationId));
    }

    private Task RequestRowAsync(RankingItemDto row, ChangeEventArgs e)
    {
        var target = e.Value?.ToString();
        if (TableLocked || !ApplicationStages.IsValid(target) || (target == StageOf(row) && target != ApplicationStages.Rejected)) return Task.CompletedTask;
        return PrepareAsync(target!, [new Snapshot(row.ApplicationId, StageOf(row), VersionOf(row))], true);
    }
    private Task RequestBulkAsync(string target) => TableLocked || selected.Count == 0 ? Task.CompletedTask
        : PrepareAsync(target, VisibleRows.Where(r => selected.Contains(r.ApplicationId)).Select(r => new Snapshot(r.ApplicationId, StageOf(r), VersionOf(r))).ToList(), false);

    private async Task PrepareAsync(string target, List<Snapshot> rows, bool single)
    {
        if (rows.Count is 0 or > 200) return;
        pending = new(target, rows, single);
        showDecisionDialog = target == ApplicationStages.Rejected || !ActorState.HasActor;
        await OnInteractionChanged.InvokeAsync(true);
        if (target != ApplicationStages.Rejected && ActorState.HasActor)
            await ConfirmDecisionAsync(new(target, null, null, ActorState.Actor!));
    }

    private async Task SaveActorAsync(StageDialog.Decision decision)
    {
        await ActorState.SetAsync(decision.Actor);
        editingActor = false;
    }

    private async Task CancelPendingAsync()
    {
        if (busy) return;
        pending = null;
        selectVersion++; // Recreate the select so a cancelled DOM selection returns to its saved value.
        await OnInteractionChanged.InvokeAsync(false);
    }

    private async Task ConfirmDecisionAsync(StageDialog.Decision choice)
    {
        if (pending is null || busy || disposed) return;
        busy = true;
        message = null;
        var decision = pending; // Frozen stages/versions from when HR opened the decision.
        try
        {
            await ActorState.SetAsync(choice.Actor);
            if (decision.Single)
            {
                var row = decision.Rows[0];
                var response = await ApplicationApi.ChangeStageAsync(row.Id, new ChangeStageRequest
                {
                    ToStage = choice.ToStage, RejectReason = choice.RejectReason, Note = choice.Note, Actor = choice.Actor,
                    ExpectedStage = row.Stage, ExpectedStageVersion = row.Version == Guid.Empty ? null : row.Version
                }, lifetime.Token);
                if (disposed) return;
                if (response.IsSuccess)
                {
                    overrides[row.Id] = response.Data!;
                    Show(response.Data!.Outcome == "Unchanged" ? "Hồ sơ đã ở giai đoạn và lý do này. Không tạo quyết định mới."
                        : $"Đã chuyển hồ sơ sang {StageText.Name(response.Data.Stage)}.", false);
                }
                else ShowFailure(response.ErrorMessage, response.StatusCode);
            }
            else
            {
                var response = await ApplicationApi.ChangeStagesAsync(new BulkChangeStageRequest
                {
                    ApplicationIds = decision.Rows.Select(r => r.Id).ToList(), ToStage = choice.ToStage,
                    RejectReason = choice.RejectReason, Note = choice.Note, Actor = choice.Actor,
                    ExpectedStageVersions = decision.Rows.Where(r => r.Version != Guid.Empty).ToDictionary(r => r.Id, r => r.Version)
                }, lifetime.Token);
                if (disposed) return;
                if (response.IsSuccess)
                {
                    var data = response.Data!;
                    foreach (var row in data.Results.Where(r => r.Stage is not null && r.Outcome is "Changed" or "Unchanged")) overrides[row.ApplicationId] = row;
                    selected.Clear();
                    var missing = data.Results.Where(r => r.Outcome == "NotFound").Select(r => $"#{r.ApplicationId}").ToList();
                    Show($"Đã chuyển {data.Changed} hồ sơ sang {StageText.Name(choice.ToStage)}; {data.Unchanged} hồ sơ đã ở giai đoạn này; {data.NotFound} hồ sơ không còn tồn tại."
                        + (missing.Count > 0 ? $" Không tìm thấy: {string.Join(", ", missing)}." : ""), data.NotFound > 0);
                }
                else ShowFailure(response.ErrorMessage, response.StatusCode);
            }
            // Never retry a write automatically, including timeout or disconnected requests.
            pending = null;
            selectVersion++;
            StateHasChanged();
            needsSync = !await SynchronizeAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            busy = false;
            if (!disposed) await OnInteractionChanged.InvokeAsync(false);
        }
    }

    private Task<bool> SynchronizeAsync() => ReloadStages?.Invoke() ?? Task.FromResult(true);
    private async Task RetrySyncAsync()
    {
        if (busy) return;
        busy = true;
        await OnInteractionChanged.InvokeAsync(true);
        try { needsSync = !await SynchronizeAsync(); }
        finally { busy = false; await OnInteractionChanged.InvokeAsync(false); }
    }
    private void ShowFailure(string? detail, int? status)
    {
        if (Anonymity.Enabled)
        {
            Show(status switch
            {
                409 => "Hồ sơ vừa được HR khác thay đổi. Hệ thống sẽ tải lại; hãy kiểm tra quyết định mới trước khi thao tác tiếp.",
                400 => "Dữ liệu quyết định không hợp lệ. Hãy kiểm tra giai đoạn, lý do và ghi chú.",
                404 => "Hồ sơ không còn tồn tại. Hãy tải lại bảng xếp hạng.",
                null => "Chưa xác nhận được kết quả lưu. Hệ thống sẽ tải lại; không tự gửi lại quyết định.",
                _ => "Máy chủ chưa xử lý được quyết định. Hãy kiểm tra dữ liệu mới trước khi thử lại."
            }, true);
            return;
        }
        var text = status is >= 500 ? "Máy chủ gặp lỗi khi xử lý quyết định. Hãy kiểm tra dữ liệu mới trước khi thử lại." : detail ?? "Không đổi được giai đoạn.";
        if (status == 409) text = "Hồ sơ vừa được đổi. " + text;
        if (status is null) text += " Chưa xác nhận được kết quả lưu; hệ thống sẽ tải lại dữ liệu. Không tự gửi lại quyết định.";
        Show(text, true);
    }
    private void Show(string text, bool error) { message = text; messageIsError = error; }
    public void Dispose() { disposed = true; Anonymity.Changed -= PrivacyChanged; lifetime.Cancel(); lifetime.Dispose(); }
}
