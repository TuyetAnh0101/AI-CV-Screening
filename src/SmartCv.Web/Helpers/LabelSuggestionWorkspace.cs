using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>Explicit HR writes; a failed or timed out write must be reconciled by reads before another PUT.</summary>
public sealed class LabelSuggestionWorkspace(PromptApiClient api, int setId) : IDisposable
{
    public int SetId { get; } = setId;
    public LabelSuggestionDraft Draft { get; } = new();
    public string Actor { get; set; } = "";
    public string? Error { get; private set; }
    public string? Message { get; private set; }
    public SaveHrLabelsResult? Saved { get; private set; }
    public bool Loading { get; private set; }
    public bool Saving { get; private set; }
    public bool NeedsVerification { get; private set; }
    public bool Fresh { get; private set; }
    public DateTimeOffset? RetryAfter { get; private set; }
    public bool Busy => Loading || Saving;
    public bool CanSave => !Busy && Fresh && !NeedsVerification && Draft.HasPending && LabelSuggestionSelection.ValidateActor(Actor) is null
        && (RetryAfter is null || DateTimeOffset.UtcNow >= RetryAfter);
    private CancellationTokenSource? read;
    private readonly CancellationTokenSource lifetime = new();
    private int generation;
    private bool disposed;
    private SaveHrLabelsRequest? uncertain;

    public void CancelRead()
    {
        if (!Loading) return;
        ++generation; read?.Cancel(); Loading = false; Fresh = false;
    }

    public async Task<bool> LoadAsync()
    {
        if (disposed || Busy) return false;
        read?.Dispose(); read = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = read.Token; var current = ++generation;
        Loading = true; Fresh = false; Error = null;
        try
        {
            // A read of labels also handles a decision which no longer appears in suggestions.
            EvaluationSetDetailDto? detail = null;
            if (NeedsVerification)
            {
                var labels = await api.GetEvaluationSetAsync(SetId, ct);
                if (disposed || current != generation) return false;
                if (!labels.IsSuccess || labels.Data!.EvaluationSetId != SetId)
                { Error = "Chưa xác minh được nhãn trên máy chủ. Chỉ đọc lại trạng thái trước khi lưu tiếp."; return false; }
                detail = labels.Data;
            }
            var result = await api.GetLabelSuggestionsAsync(SetId, ct: ct);
            if (disposed || current != generation) return false;
            if (!result.IsSuccess)
            { Error = $"Không tải được đề xuất nhãn HR{(result.StatusCode is { } code ? $" (HTTP {code})" : " do mất kết nối hoặc quá thời gian chờ")}. Hãy thử đọc lại."; return false; }
            if (result.Data!.EvaluationSetId != SetId || result.Data.JobId is not null)
            { Error = "API trả sai bộ CV hoặc phạm vi đề xuất. Hãy thử tải lại."; return false; }
            var candidates = await api.GetLabelCandidatesAsync(SetId, ct);
            if (disposed || current != generation) return false;
            if (!candidates.IsSuccess)
            { Error = "Chưa xác minh được hồ sơ còn sẵn sàng. Hãy đọc lại danh sách trước khi duyệt nhãn."; return false; }
            Draft.Load(result.Data, LabelSuggestionSelection.AvailableIds(candidates.Data!)); Fresh = true;
            if (NeedsVerification)
            {
                var confirmed = uncertain is { } sent && sent.Labels.All(input => detail!.Labels.Any(l =>
                    l.ApplicationId == input.ApplicationId && l.IsPass == input.IsPass && l.Note == input.Note && l.LabeledBy == sent.LabeledBy));
                Message = confirmed ? "Máy chủ hiện có đủ các nhãn vừa gửi. Đã đọc lại trạng thái; không gửi lại yêu cầu lưu."
                    : "Đã đọc lại trạng thái máy chủ. Hãy kiểm tra và chọn lại nhãn cần lưu; yêu cầu trước chưa được xác nhận thành công.";
                // Require a fresh, deliberate selection after an ambiguous write or conflict.
                foreach (var item in Draft.PickedItems.ToList()) Draft.Toggle(item, false);
                NeedsVerification = false; uncertain = null;
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        finally { if (current == generation) Loading = false; }
    }

    /// <returns>True when label-dependent views should refresh, including reconciled uncertain writes.</returns>
    public async Task<bool> SaveAsync()
    {
        if (disposed || Busy || NeedsVerification || !Fresh) return false;
        if (RetryAfter is { } retry && DateTimeOffset.UtcNow < retry)
        { Error = "Máy chủ yêu cầu chờ trước khi lưu tiếp. Hãy tải lại đề xuất sau ít phút."; return false; }
        var (body, error) = Draft.BuildRequest(Actor);
        Error = error; Message = null; Saved = null;
        if (body is null) return false;
        Saving = true;
        try
        {
            // Suggestions omit extraction/retention fields. Recheck the existing candidate contract before any write.
            var candidates = await api.GetLabelCandidatesAsync(SetId, lifetime.Token);
            if (disposed) return false;
            var available = candidates.IsSuccess ? LabelSuggestionSelection.AvailableIds(candidates.Data!) : [];
            if (!candidates.IsSuccess || body.Labels.Any(l => !available.Contains(l.ApplicationId)))
            { Fresh = false; Error = "Hồ sơ vừa thay đổi hoặc chưa xác minh được trạng thái sẵn sàng. Chưa gửi nhãn; hãy lấy lại đề xuất."; return false; }
            // Mark uncertainty before sending, including cancellation after the server commits.
            uncertain = body; NeedsVerification = true;
            var result = await api.SaveLabelsAsync(SetId, body, lifetime.Token);
            if (disposed) return false;
            if (!result.IsSuccess)
            {
                var failure = result.StatusCode switch
                {
                    400 => "Nhãn không hợp lệ. Kiểm tra lựa chọn và tên HR.",
                    409 => "Dữ liệu đã thay đổi. Cần đọc lại và duyệt lựa chọn trước khi lưu tiếp.",
                    429 => "Quá nhiều yêu cầu. Chờ theo hướng dẫn máy chủ rồi thử lại.",
                    _ => "Chưa xác nhận được việc lưu. Máy chủ có thể đã lưu; cần đọc lại trạng thái."
                };
                if (result.StatusCode is 400 or 429) { NeedsVerification = false; uncertain = null; }
                if (result.StatusCode == 429 && result.RetryAfter is { } delay) RetryAfter = DateTimeOffset.UtcNow + delay;
                Saving = false;
                var reconciled = NeedsVerification && await LoadAsync();
                // Server problem details may contain candidate identifiers; keep presentation text controlled.
                Error = failure + (Error is null ? "" : " " + Error);
                return reconciled;
            }
            Saved = result.Data; NeedsVerification = false; uncertain = null;
            Message = $"Đã lưu: thêm {Saved!.Added}, sửa {Saved.Updated}, giữ nguyên {Saved.Unchanged} nhãn. Bộ hiện có {Saved.LabelCount} CV có nhãn.";
            foreach (var item in Draft.PickedItems.ToList()) Draft.Toggle(item, false);
            Saving = false;
            await LoadAsync();
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return false; }
        finally { Saving = false; }
    }

    public void Dispose()
    { disposed = true; ++generation; lifetime.Cancel(); read?.Cancel(); read?.Dispose(); lifetime.Dispose(); }
}
