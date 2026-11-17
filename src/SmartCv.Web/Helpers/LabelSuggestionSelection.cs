using SmartCv.Core.Applications;
using SmartCv.Core.Contracts;
using SmartCv.Core.Evaluation;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B41 (A36): quy tắc chọn đề xuất nhãn HR trước khi lưu.
///   New      → tích sẵn (hồ sơ chưa có nhãn trong bộ)
///   Conflict → KHÔNG tích sẵn, tô vàng: nhãn hiện tại khác quyết định mới, HR phải tự xem rồi mới tích
///   Same     → không tích được (đã có nhãn giống)
/// Hồ sơ "không đề xuất" (rút hồ sơ, tuyển đủ, trùng, đang xem, dự phòng) không có trong danh sách của API.
/// Đề xuất chỉ dựa trên quyết định HR, không bao giờ dựa trên điểm AI.
/// </summary>
public static class LabelSuggestionSelection
{
    public const string New = "New", Same = "Same", Conflict = "Conflict";
    public const int MaxLabeledByLength = 100;     // A15: NVARCHAR(100)
    public const int MaxNoteLength = 500;          // A15: NVARCHAR(500)
    public const int MaxLabelsPerRequest = 2000;   // A15: tối đa mỗi lần PUT
    public const string ConflictText = "Khác quyết định mới — HR xem lại trước khi tích";

    public static bool DefaultChecked(LabelSuggestionDto s) => s.Status == New && CanCheck(s);

    /// <summary>Chỉ New/Conflict có mã hồ sơ hợp lệ; trạng thái lạ được coi như không chọn được (an toàn).</summary>
    public static bool CanCheck(LabelSuggestionDto s) => s.ApplicationId > 0 && s.Status is New or Conflict
        && s.FileName != SmartCv.Core.Privacy.RetentionRules.AnonymizedFileName
        && LabelSuggestionRules.Suggest(s.Stage, s.RejectReason) == s.SuggestedIsPass
        && (s.Status == New ? s.CurrentLabel is null : s.CurrentLabel is not null && s.CurrentLabel != s.SuggestedIsPass);

    public static bool IsConflict(LabelSuggestionDto s) => s.Status == Conflict;

    public static bool Available(LabelCandidateDto c) => c.ApplicationId > 0 && c.ExtractionStatus == "Completed"
        && c.HasExtractedText && c.FileName != SmartCv.Core.Privacy.RetentionRules.AnonymizedFileName;

    public static HashSet<int> AvailableIds(IEnumerable<LabelCandidateDto> candidates) => candidates
        .GroupBy(c => c.ApplicationId).Where(g => g.Count() == 1 && Available(g.Single())).Select(g => g.Key).ToHashSet();

    public static string LabelText(bool? isPass) => isPass switch { true => "Đạt", false => "Không đạt", _ => "Chưa có" };

    public static string StatusText(string status) => status switch
    {
        New => "Đề xuất mới",
        Conflict => "Khác nhãn hiện tại",
        Same => "Đã có nhãn giống",
        _ => "Không xác định"
    };

    /// <summary>Lý do gọn ("Loại: Thiếu kỹ năng bắt buộc") dựng lại từ Stage/RejectReason; API có thể nối thêm câu giải thích xung đột.</summary>
    public static string Decision(LabelSuggestionDto s) =>
        string.IsNullOrWhiteSpace(s.Stage) ? s.Reason.Trim() : LabelSuggestionRules.Explain(s.Stage, s.RejectReason);

    /// <summary>Ghi chú lưu kèm nhãn: "Từ quyết định: …", cắt đúng giới hạn 500 ký tự của A15.</summary>
    public static string NoteFor(LabelSuggestionDto s)
    {
        var note = $"Từ quyết định: {Decision(s)}".Trim();
        return note.Length <= MaxNoteLength ? note : note[..MaxNoteLength];
    }

    /// <summary>null nếu hợp lệ; ngược lại câu tiếng Việt. Giới hạn = min(A15 LabeledBy, tên HR của B34).</summary>
    public static string? ValidateActor(string? labeledBy)
    {
        var name = labeledBy?.Trim() ?? "";
        var max = Math.Min(MaxLabeledByLength, StageChangeRules.MaxActorLength);
        return name.Length == 0 ? "Nhập tên người gán nhãn trước khi lưu."
            : name.Length > max ? $"Tên người gán nhãn tối đa {max} ký tự." : null;
    }

    /// <summary>
    /// Body cho PUT /api/evaluation-sets/{id}/labels (A15) từ các dòng HR đã tích.
    /// Bỏ dòng không chọn được và mã hồ sơ trùng; không bao giờ tạo request rỗng.
    /// </summary>
    public static (SaveHrLabelsRequest? Request, string? Error) BuildRequest(IEnumerable<LabelSuggestionDto> picked, string? labeledBy)
    {
        if (ValidateActor(labeledBy) is { } actorError) return (null, actorError);
        var rows = picked.Where(CanCheck).GroupBy(s => s.ApplicationId).Where(g => g.Count() == 1).Select(g => g.Single()).ToList();
        if (rows.Count == 0) return (null, "Chưa chọn đề xuất nào để lưu.");
        if (rows.Count > MaxLabelsPerRequest) return (null, $"Mỗi lần lưu tối đa {MaxLabelsPerRequest} nhãn. Hãy bỏ bớt lựa chọn.");
        return (new SaveHrLabelsRequest
        {
            LabeledBy = labeledBy!.Trim(),
            Labels = rows.Select(s => new HrLabelInput { ApplicationId = s.ApplicationId, IsPass = s.SuggestedIsPass, Note = NoteFor(s) }).ToList()
        }, null);
    }
}

/// <summary>
/// B41: lựa chọn đề xuất của HR. Lựa chọn HR tự bấm được nhớ theo (trạng thái, đề xuất, nhãn hiện tại) nên
/// "Lấy lại đề xuất" hoặc đổi bộ lọc không làm mất lựa chọn còn hợp lệ; dòng đã đổi trạng thái quay về mặc định.
/// </summary>
public sealed class LabelSuggestionDraft
{
    private readonly Dictionary<int, (string Signature, bool Picked)> choices = [];
    private readonly HashSet<int> picked = [];
    private HashSet<int> ambiguous = [];
    private HashSet<int>? available;

    public LabelSuggestionResponse? Data { get; private set; }
    public int PickedCount => picked.Count;
    public bool HasPending => picked.Count > 0;
    public int AmbiguousCount => ambiguous.Count;

    public IEnumerable<LabelSuggestionDto> Items => Data?.Items ?? [];
    public IEnumerable<LabelSuggestionDto> PickedItems => Items.Where(s => picked.Contains(s.ApplicationId) && Selectable(s));
    public int PickedConflicts => PickedItems.Count(LabelSuggestionSelection.IsConflict);
    public IEnumerable<LabelSuggestionDto> NewItems => Items.Where(s => Selectable(s) && LabelSuggestionSelection.DefaultChecked(s));
    public bool AllNewPicked => NewItems.Any() && NewItems.All(s => picked.Contains(s.ApplicationId));

    /// <summary>Hồ sơ xuất hiện hai lần trong phản hồi là dữ liệu mâu thuẫn: không cho chọn (A15 sẽ từ chối "Trùng hồ sơ").</summary>
    public bool Selectable(LabelSuggestionDto s) => LabelSuggestionSelection.CanCheck(s) && !ambiguous.Contains(s.ApplicationId)
        && (available is null || available.Contains(s.ApplicationId));

    public bool IsPicked(LabelSuggestionDto s) => Selectable(s) && picked.Contains(s.ApplicationId);

    public void Load(LabelSuggestionResponse data, IReadOnlySet<int>? availableIds = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        available = availableIds?.ToHashSet();
        ambiguous = data.Items.GroupBy(s => s.ApplicationId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        picked.Clear();
        foreach (var s in data.Items.Where(Selectable))
        {
            var on = choices.TryGetValue(s.ApplicationId, out var c) && c.Signature == Signature(s)
                ? c.Picked : LabelSuggestionSelection.DefaultChecked(s);
            if (on) picked.Add(s.ApplicationId);
        }
    }

    public void Toggle(LabelSuggestionDto s, bool on)
    {
        // Chỉ nhận dòng đang có trong phản hồi hiện tại (không chọn hồ sơ của bộ lọc/lần tải khác).
        var item = Items.FirstOrDefault(i => i.ApplicationId == s.ApplicationId);
        if (item is null || !Selectable(item) || Signature(item) != Signature(s)) return;
        choices[item.ApplicationId] = (Signature(item), on);
        if (on) picked.Add(item.ApplicationId); else picked.Remove(item.ApplicationId);
    }

    /// <summary>"Chọn tất cả đề xuất mới": KHÔNG chạm tới dòng xung đột — HR phải tích từng dòng sau khi xem.</summary>
    public void ToggleAllNew(bool on)
    {
        foreach (var s in NewItems.ToList()) Toggle(s, on);
    }

    /// <summary>Sau khi lưu thành công: các dòng đã lưu sẽ thành Same, quên lựa chọn cũ.</summary>
    public void ForgetChoices() => choices.Clear();

    public (SaveHrLabelsRequest? Request, string? Error) BuildRequest(string? labeledBy) =>
        LabelSuggestionSelection.BuildRequest(PickedItems, labeledBy);

    private static string Signature(LabelSuggestionDto s) => $"{s.Status}|{s.SuggestedIsPass}|{s.CurrentLabel}|{s.Stage}|{s.RejectReason}|{s.StageUpdatedAt:O}|{s.CurrentNote}|{s.CurrentLabeledBy}";
}
