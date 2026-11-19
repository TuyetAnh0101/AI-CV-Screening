using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>
/// SCApp v2: các bước 2–4 của một bộ CV đánh giá.
///   Bước 2: chọn nhiều hồ sơ đã quét (không upload lại) và thêm vào bộ — API bỏ qua hồ sơ trùng.
///   Bước 3: HR gán Đạt / Không đạt / Chưa đánh giá cho từng thành viên. Không bao giờ lấy dự đoán AI làm nhãn.
///   Bước 4: số liệu kiểm tra bộ do API tính (Readiness).
/// Bản nháp nhãn/ghi chú được giữ nguyên khi lưu lỗi hoặc tải lại thành viên.
/// </summary>
public sealed class SetMembersWorkspace(PromptApiClient api)
{
    public const string Pass = "pass", Fail = "fail", Unlabeled = "";

    public sealed class MemberRow(EvaluationSetMemberDto member)
    {
        public EvaluationSetMemberDto Member { get; private set; } = member;
        public string Choice { get; set; } = ChoiceOf(member.IsPass);
        public string Note { get; set; } = member.Note ?? "";
        private string savedChoice = ChoiceOf(member.IsPass), savedNote = member.Note ?? "";
        public bool IsDirty => Choice != savedChoice || (Choice != Unlabeled && Note != savedNote);
        /// <summary>Đổi từ có nhãn về "Chưa đánh giá": cần gọi API bỏ nhãn, không gửi trong PUT labels.</summary>
        public bool ClearsLabel => savedChoice != Unlabeled && Choice == Unlabeled;
        public bool CanLabel => Member.IsReady;
        internal void Refresh(EvaluationSetMemberDto fresh, bool keepDraft)
        {
            Member = fresh; savedChoice = ChoiceOf(fresh.IsPass); savedNote = fresh.Note ?? "";
            if (!keepDraft) { Choice = savedChoice; Note = savedNote; }
        }
        private static string ChoiceOf(bool? isPass) => isPass is null ? Unlabeled : isPass.Value ? Pass : Fail;
    }

    public int SetId { get; private set; }
    public EvaluationSetDetailDto? Detail { get; private set; }
    public List<MemberRow> Rows { get; private set; } = [];
    public EvaluationSetReadinessDto Readiness => Detail?.Readiness ?? new();
    public bool Busy { get; private set; }
    public bool Loaded { get; private set; }
    public string? Error { get; private set; }
    public string? Message { get; private set; }
    public List<string> Warnings { get; private set; } = [];
    public bool IsDirty => Rows.Any(r => r.IsDirty);
    public int DirtyCount => Rows.Count(r => r.IsDirty);

    // ----- Bước 2: chọn CV đã quét -----
    public CandidatePickerFilter Filter { get; set; } = new();
    public string Search { get; set; } = "";
    public List<LabelCandidateDto> Candidates { get; private set; } = [];
    public bool CandidatesLoaded { get; private set; }
    public HashSet<int> Selected { get; } = [];
    public IEnumerable<LabelCandidateDto> VisibleCandidates(Func<LabelCandidateDto, string> name) => Candidates
        .Where(c => !c.IsMember && (string.IsNullOrWhiteSpace(Search) || RankingView.Fold(name(c) + " " + c.FileName + " " + c.JobTitle)
            .Contains(RankingView.Fold(Search.Trim()), StringComparison.Ordinal)));

    public async Task LoadAsync(int setId, CancellationToken ct)
    {
        if (Busy || setId <= 0) return;
        var changedSet = setId != SetId;
        Busy = true; Error = null;
        if (changedSet) { Message = null; Warnings = []; Selected.Clear(); Candidates = []; CandidatesLoaded = false; Filter = new(); Search = ""; }
        try
        {
            var detail = await api.GetEvaluationSetAsync(setId, ct);
            if (!detail.IsSuccess) { Error = detail.ErrorMessage; if (changedSet) { Rows = []; Detail = null; Loaded = false; } return; }
            if (detail.Data!.EvaluationSetId != setId) { Error = "API trả về sai bộ CV."; return; }
            SetId = setId;
            Apply(detail.Data, keepDrafts: !changedSet);
            Loaded = true;
        }
        finally { Busy = false; }
    }

    /// <summary>Giữ nguyên bản nháp của hồ sơ đang sửa; chỉ làm mới dữ liệu máy chủ.</summary>
    private void Apply(EvaluationSetDetailDto detail, bool keepDrafts)
    {
        var old = Rows.ToDictionary(r => r.Member.ApplicationId);
        Detail = detail;
        Rows = detail.Members.Select(m =>
        {
            if (keepDrafts && old.TryGetValue(m.ApplicationId, out var row)) { row.Refresh(m, keepDraft: row.IsDirty); return row; }
            return new MemberRow(m);
        }).ToList();
    }

    public async Task LoadCandidatesAsync(CancellationToken ct)
    {
        if (Busy || SetId <= 0) return;
        Busy = true; Error = null;
        try
        {
            var result = await api.GetSetCandidatesAsync(SetId, Filter, ct);
            if (!result.IsSuccess) { Error = result.ErrorMessage; return; }
            Candidates = result.Data!.GroupBy(c => c.ApplicationId).Select(g => g.First()).ToList();
            Selected.IntersectWith(Candidates.Where(c => !c.IsMember).Select(c => c.ApplicationId));
            CandidatesLoaded = true;
        }
        finally { Busy = false; }
    }

    public void Toggle(int applicationId, bool on)
    {
        if (on && Candidates.Any(c => c.ApplicationId == applicationId && !c.IsMember && !c.IsAnonymized)) Selected.Add(applicationId);
        else Selected.Remove(applicationId);
    }

    public async Task<bool> AddSelectedAsync(string addedBy, CancellationToken ct)
    {
        if (Busy || SetId <= 0) return false;
        Error = Message = null; Warnings = [];
        var ids = Selected.ToList();
        if (ids.Count == 0) { Error = "Chọn ít nhất một hồ sơ để thêm vào bộ."; return false; }
        if (ids.Count > 500) { Error = "Mỗi lần thêm tối đa 500 hồ sơ. Hãy lọc bớt."; return false; }
        if (string.IsNullOrWhiteSpace(addedBy) || addedBy.Trim().Length > 100) { Error = "Nhập tên người thao tác (tối đa 100 ký tự)."; return false; }
        Busy = true;
        try
        {
            var result = await api.AddMembersAsync(SetId, new() { ApplicationIds = ids, AddedBy = addedBy.Trim() }, ct);
            if (!result.IsSuccess) { Error = result.ErrorMessage; return false; }
            var data = result.Data!;
            Selected.Clear();
            var duplicate = data.Skipped.Count(s => s.Reason == SkippedMemberReasons.AlreadyMember);
            var other = data.Skipped.Count - duplicate;
            Message = $"Đã thêm {data.Added} hồ sơ vào bộ (tổng {data.MemberCount})."
                + (duplicate > 0 ? $" {duplicate} hồ sơ đã có trong bộ nên được bỏ qua." : "")
                + (other > 0 ? $" {other} hồ sơ không thêm được: {string.Join(" ", data.Skipped.Where(s => s.Reason != SkippedMemberReasons.AlreadyMember).Take(3).Select(s => s.Message))}" : "")
                + " Tiếp theo: gán nhãn HR cho các hồ sơ mới.";
            Warnings = data.Warnings;
        }
        finally { Busy = false; }
        await ReloadAfterWriteAsync(ct);
        return true;
    }

    public async Task<bool> RemoveAsync(int applicationId, CancellationToken ct)
    {
        if (Busy || SetId <= 0) return false;
        Error = Message = null; Warnings = [];
        Busy = true;
        try
        {
            var result = await api.RemoveMemberAsync(SetId, applicationId, ct);
            if (!result.IsSuccess) { Error = result.ErrorMessage; return false; }
            Rows.RemoveAll(r => r.Member.ApplicationId == applicationId);
            Message = $"Đã bỏ hồ sơ #{applicationId} khỏi bộ" + (result.Data!.RemovedLabel ? " cùng nhãn HR của hồ sơ trong bộ này." : ".");
            Warnings = result.Data.Warnings;
        }
        finally { Busy = false; }
        await ReloadAfterWriteAsync(ct);
        return true;
    }

    /// <summary>Lưu các nhãn đã đổi. "Chưa đánh giá" gọi API bỏ nhãn riêng từng hồ sơ; hồ sơ chưa sẵn sàng không được gửi.</summary>
    public async Task<bool> SaveLabelsAsync(string labeledBy, CancellationToken ct)
    {
        if (Busy || SetId <= 0) return false;
        Error = Message = null; Warnings = [];
        var changed = Rows.Where(r => r.IsDirty).ToList();
        var labels = changed.Where(r => r.Choice is Pass or Fail && r.CanLabel).ToList();
        var clears = changed.Where(r => r.ClearsLabel).ToList();
        if (labels.Count == 0 && clears.Count == 0) { Error = "Chưa có thay đổi nhãn nào để lưu."; return false; }
        if (changed.Any(r => r.Choice is Pass or Fail && !r.CanLabel)) { Error = "Có hồ sơ chưa sẵn sàng (CV chưa đọc xong hoặc đã ẩn danh): không gán nhãn được."; return false; }
        if (string.IsNullOrWhiteSpace(labeledBy) || labeledBy.Trim().Length > 100) { Error = "Nhập tên người gán nhãn (tối đa 100 ký tự)."; return false; }
        if (labels.Any(r => r.Note.Length > 500)) { Error = "Ghi chú tối đa 500 ký tự mỗi hồ sơ."; return false; }
        Busy = true;
        var failures = new List<string>();
        try
        {
            if (labels.Count > 0)
            {
                var result = await api.SaveLabelsAsync(SetId, new()
                {
                    LabeledBy = labeledBy.Trim(),
                    Labels = labels.Select(r => new HrLabelInput { ApplicationId = r.Member.ApplicationId, IsPass = r.Choice == Pass, Note = r.Note }).ToList()
                }, ct);
                if (!result.IsSuccess) { Error = result.ErrorMessage; return false; }
                Warnings = result.Data!.Warnings;
            }
            foreach (var row in clears)
            {
                var result = await api.ClearLabelAsync(SetId, row.Member.ApplicationId, ct);
                if (!result.IsSuccess) failures.Add($"#{row.Member.ApplicationId}: {result.ErrorMessage}");
            }
            Message = $"Đã lưu {labels.Count} nhãn" + (clears.Count > 0 ? $" và đưa {clears.Count - failures.Count} hồ sơ về Chưa đánh giá" : "") + ".";
            if (failures.Count > 0) Error = "Chưa bỏ được nhãn: " + string.Join("; ", failures.Take(3));
        }
        finally { Busy = false; }
        // Lưu thành công: nhãn đã lưu không còn là bản nháp. Hồ sơ lỗi giữ bản nháp để thử lại.
        var failedIds = clears.Where(r => failures.Any(f => f.StartsWith($"#{r.Member.ApplicationId}:", StringComparison.Ordinal))).Select(r => r.Member.ApplicationId).ToHashSet();
        foreach (var row in changed.Where(r => !failedIds.Contains(r.Member.ApplicationId)))
            row.Refresh(new EvaluationSetMemberDto
            {
                ApplicationId = row.Member.ApplicationId, CvId = row.Member.CvId, CandidateName = row.Member.CandidateName, FileName = row.Member.FileName,
                JobId = row.Member.JobId, JobTitle = row.Member.JobTitle, Stage = row.Member.Stage, ExtractionStatus = row.Member.ExtractionStatus,
                HasExtractedText = row.Member.HasExtractedText, IsAnonymized = row.Member.IsAnonymized, IsReady = row.Member.IsReady,
                NotReadyReason = row.Member.NotReadyReason, IsPass = row.Choice == Unlabeled ? null : row.Choice == Pass,
                Note = row.Choice == Unlabeled ? null : row.Note, AddedBy = row.Member.AddedBy, AddedAt = row.Member.AddedAt
            }, keepDraft: false);
        await ReloadAfterWriteAsync(ct);
        return failures.Count == 0;
    }

    /// <summary>Đọc lại thành viên + số liệu sau khi ghi; lỗi đọc không làm mất bản nháp.</summary>
    private async Task ReloadAfterWriteAsync(CancellationToken ct)
    {
        Busy = true;
        try
        {
            var detail = await api.GetEvaluationSetAsync(SetId, ct);
            if (detail.IsSuccess && detail.Data!.EvaluationSetId == SetId) Apply(detail.Data, keepDrafts: true);
            else Error ??= "Đã lưu nhưng chưa tải lại được bộ CV. Hãy bấm Tải lại.";
            if (CandidatesLoaded)
            {
                var list = await api.GetSetCandidatesAsync(SetId, Filter, ct);
                if (list.IsSuccess) Candidates = list.Data!.GroupBy(c => c.ApplicationId).Select(g => g.First()).ToList();
            }
        }
        finally { Busy = false; }
    }
}
