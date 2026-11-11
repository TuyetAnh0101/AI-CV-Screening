using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>B14 state machine. An uncertain POST remains locked across reloads via its query token.</summary>
public sealed class PromptTestFlow(PromptApiClient api, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset lastProgress;
    public List<EvaluationSetDto> Sets { get; private set; } = [];
    public PromptTestRunDto? Current { get; private set; }
    public bool SetsLoaded { get; private set; }
    public string? SetsError { get; private set; }
    public string Token { get; private set; } = "";
    public string? Error { get; private set; }
    public bool Checking { get; private set; }
    public bool Starting { get; private set; }
    public bool Uncertain { get; private set; }
    public bool Ready { get; private set; }
    public bool Running => Current?.Status is "Queued" or "Running";
    public bool Locked => Checking || Starting || Uncertain || Running || !Ready;
    public Func<Task>? Changed { get; set; }
    private Task Notify() => Changed?.Invoke() ?? Task.CompletedTask;
    public static decimal Percent(PromptTestRunDto run) => run.TotalItems <= 0 ? 0 : Math.Clamp(run.ProcessedItems * 100m / run.TotalItems, 0, 100);
    public static bool ValidSet(EvaluationSetDto? set) => set is { LabelCount: > 1, PassCount: > 0, UnreadyCount: 0 } && set.PassCount < set.LabelCount;
    public async Task ReloadSetsAsync(CancellationToken ct)
    {
        var result = await api.GetEvaluationSetsAsync(ct);
        SetsLoaded = result.IsSuccess; SetsError = result.IsSuccess ? null : result.ErrorMessage;
        if (result.IsSuccess) Sets = result.Data!;
        Ready = SetsLoaded && Error is null && !Uncertain;
    }

    public async Task RestoreAsync(string? token, CancellationToken ct)
    {
        if (Checking || Starting) return;
        Checking = true; Ready = false; Error = null; Token = token ?? ""; Current = null; Uncertain = false;
        try
        {
            await ReloadSetsAsync(ct);
            if (int.TryParse(Token, out var id) && id > 0)
                await ReadAsync(id, ct);
            else
                await DiscoverAsync(ct);
            Ready = Error is null && SetsLoaded;
        }
        finally { Checking = false; await Notify(); }
    }
    private async Task DiscoverAsync(CancellationToken ct)
    {
        var list = await api.GetTestsAsync(ct);
        if (!list.IsSuccess) { Error = list.ErrorMessage; return; }
        var parts = Token.Split('.');
        PromptTestRunDto? found;
        if (parts.Length == 4 && parts[0] == "pending" && int.TryParse(parts[1], out var p) && int.TryParse(parts[2], out var s) && int.TryParse(parts[3], out var last))
        {
            found = list.Data!.Where(r => r.PromptVersionId == p && r.EvaluationSetId == s && (r.RunId > last || r.Status is "Queued" or "Running")).OrderByDescending(r => r.RunId).FirstOrDefault();
            Uncertain = found is null;
            if (Uncertain) Error = "Chưa xác định được yêu cầu trước đã tạo lượt hay chưa. Chỉ kiểm tra lại trạng thái; không gửi lại yêu cầu chạy.";
        }
        else if (Token.Length > 0)
        { Uncertain = true; Error = "Địa chỉ lượt đánh giá không hợp lệ. Không thể xác nhận an toàn để tạo lượt mới."; return; }
        else found = list.Data!.Where(r => r.Status is "Queued" or "Running").OrderByDescending(r => r.RunId).FirstOrDefault();
        if (found is not null) await ReadAsync(found.RunId, ct);
    }
    private async Task ReadAsync(int id, CancellationToken ct)
    {
        var result = await api.GetTestAsync(id, ct);
        if (!result.IsSuccess) { Error = result.ErrorMessage; return; }
        if (result.Data!.RunId != id || result.Data.Status is not ("Queued" or "Running" or "Completed" or "Failed"))
        { Error = "API trả về trạng thái lượt đánh giá không hợp lệ."; Uncertain = true; return; }
        if (Current is null || Current.RunId != id || Current.Status != result.Data.Status || Current.ProcessedItems != result.Data.ProcessedItems)
            lastProgress = clock.GetUtcNow();
        Current = result.Data; Token = id.ToString(System.Globalization.CultureInfo.InvariantCulture); Uncertain = false;
        if (Running && clock.GetUtcNow() - lastProgress >= TimeSpan.FromMinutes(2))
            Error = "Chưa có tiến độ mới trong 2 phút. Kiểm tra Worker, kết nối database và trạng thái trích xuất CV, rồi bấm kiểm tra lại. Không tự tạo lượt mới.";
    }
    public async Task StartAsync(int promptId, int setId, CancellationToken ct)
    {
        if (Locked || promptId <= 0 || !ValidSet(Sets.FirstOrDefault(s => s.EvaluationSetId == setId))) return;
        Starting = true; Error = null;
        try
        {
            var list = await api.GetTestsAsync(ct);
            if (!list.IsSuccess) { Error = list.ErrorMessage; Ready = false; return; }
            var active = list.Data!.FirstOrDefault(r => r.Status is "Queued" or "Running");
            if (active is not null) { await ReadAsync(active.RunId, ct); return; }
            Current = null;
            Token = $"pending.{promptId}.{setId}.{list.Data!.Select(r => r.RunId).DefaultIfEmpty(0).Max()}";
            Uncertain = true;
            await Notify(); // Persist pending intent in the URL before spending quota.
            var result = await api.StartTestAsync(new() { PromptVersionId = promptId, EvaluationSetId = setId }, ct);
            if (result.IsSuccess && result.Data!.RunId > 0) await ReadAsync(result.Data.RunId, ct);
            else if (result.StatusCode is 400 or 404 or 429)
            { Token = ""; Uncertain = false; Error = result.ErrorMessage; }
            else { await DiscoverAsync(ct); if (Current is null) Error ??= result.ErrorMessage; }
        }
        finally { Starting = false; await Notify(); }
    }
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (Checking || Starting) return;
        Checking = true; Error = null;
        try
        {
            if (Current is not null) await ReadAsync(Current.RunId, ct);
            else await DiscoverAsync(ct);
            Ready = Error is null && SetsLoaded;
        }
        finally { Checking = false; await Notify(); }
    }
    public void ResumeTracking() => lastProgress = clock.GetUtcNow();
}
