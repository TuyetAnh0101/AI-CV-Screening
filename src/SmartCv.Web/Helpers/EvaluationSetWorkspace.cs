using SmartCv.Core.Contracts;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>Two persisted steps using A15. HR choices are explicit; AI predictions are never copied.</summary>
public sealed class EvaluationSetWorkspace(PromptApiClient api)
{
    public sealed class Row(LabelCandidateDto candidate, HrLabelDto? label)
    {
        public LabelCandidateDto Candidate { get; } = candidate;
        public string Choice { get; set; } = label is null ? "" : label.IsPass ? "pass" : "fail";
        public string Note { get; set; } = label?.Note ?? "";
        public bool HadLabel { get; private set; } = label is not null;
        public void MarkSaved() => HadLabel = true;
        public bool Eligible => Candidate.ApplicationId > 0 && Candidate.ExtractionStatus == "Completed" && Candidate.HasExtractedText;
    }
    public List<EvaluationSetDto> Sets { get; private set; } = [];
    public List<Row> Rows { get; private set; } = [];
    public int SetId { get; private set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string LabeledBy { get; set; } = "";
    public string? Error { get; private set; }
    public string? Message { get; private set; }
    public bool Busy { get; private set; }
    public bool Loaded { get; private set; }
    public bool CreationUncertain { get; private set; }
    public bool CandidatesLoaded { get; private set; }
    public EvaluationSetDto? Selected => Sets.FirstOrDefault(s => s.EvaluationSetId == SetId);

    public async Task LoadAsync(CancellationToken ct)
    {
        if (Busy) return;
        Busy = true; Error = null;
        try
        {
            var sets = await api.GetEvaluationSetsAsync(ct);
            Loaded = sets.IsSuccess;
            if (!sets.IsSuccess) { Error = sets.ErrorMessage; return; }
            Sets = sets.Data!;
            if (CreationUncertain)
            {
                var saved = Sets.FirstOrDefault(s => string.Equals(s.Name, Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (saved is null) { Error = "Chưa xác minh được yêu cầu tạo bộ trước đó. Không gửi lại POST để tránh tạo trùng; hãy kiểm tra lại danh sách."; return; }
                CreationUncertain = false; SetId = saved.EvaluationSetId;
            }
            if (SetId > 0) await LoadRowsAsync(ct);
        }
        finally { Busy = false; }
    }
    public async Task SelectAsync(int id, CancellationToken ct)
    {
        if (Busy || id <= 0 || !Sets.Any(s => s.EvaluationSetId == id)) return;
        Busy = true; Error = Message = null; SetId = id;
        try { await LoadRowsAsync(ct); }
        finally { Busy = false; }
    }
    private async Task LoadRowsAsync(CancellationToken ct)
    {
        Rows = []; CandidatesLoaded = false;
        var detail = await api.GetEvaluationSetAsync(SetId, ct);
        if (!detail.IsSuccess) { Error = detail.ErrorMessage; return; }
        if (detail.Data!.EvaluationSetId != SetId) { Error = "API trả về sai bộ CV."; return; }
        var candidates = await api.GetLabelCandidatesAsync(SetId, ct);
        if (!candidates.IsSuccess) { Error = candidates.ErrorMessage; return; }
        var labels = detail.Data.Labels.ToDictionary(l => l.ApplicationId);
        Rows = candidates.Data!.GroupBy(c => c.ApplicationId).Select(g => g.First())
            .Select(c => new Row(c, labels.GetValueOrDefault(c.ApplicationId))).ToList();
        CandidatesLoaded = true;
    }
    public async Task CreateAsync(CancellationToken ct)
    {
        if (Busy || CreationUncertain) return;
        Error = Message = null;
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 150 || Description.Length > 500)
        { Error = "Nhập tên bộ từ 1–150 ký tự và mô tả tối đa 500 ký tự."; return; }
        Busy = true;
        try
        {
            CreationUncertain = true;
            var result = await api.CreateEvaluationSetAsync(new() { Name = Name.Trim(), Description = Description }, ct);
            if (!result.IsSuccess)
            {
                if (result.StatusCode is 400 or 409 or 429) CreationUncertain = false;
                Error = result.ErrorMessage + (CreationUncertain ? " Chưa xác nhận việc tạo bộ; kiểm tra lại danh sách trước khi thử tiếp." : "");
                return;
            }
            CreationUncertain = false; SetId = result.Data!.EvaluationSetId;
            Sets = Sets.Where(s => s.EvaluationSetId != SetId).Prepend(result.Data).ToList(); Loaded = true;
            await LoadRowsAsync(ct);
            Message = "Đã lưu tên bộ CV. Tiếp theo, chọn hồ sơ và gán nhãn HR rồi lưu nhãn; bộ chưa có nhãn chưa thể chạy.";
        }
        finally { Busy = false; }
    }
    public async Task<bool> SaveAsync(CancellationToken ct)
    {
        if (Busy || !CandidatesLoaded || SetId <= 0) return false;
        Error = Message = null;
        var chosen = Rows.Where(r => r.Eligible && r.Choice is "pass" or "fail").ToList();
        if (string.IsNullOrWhiteSpace(LabeledBy) || LabeledBy.Trim().Length > 100 || chosen.Count == 0 || chosen.Count > 2000 || chosen.Any(r => r.Note.Length > 500))
        { Error = "Nhập tên HR (tối đa 100 ký tự), chọn 1–2000 hồ sơ và ghi chú tối đa 500 ký tự mỗi hồ sơ."; return false; }
        Busy = true;
        try
        {
            var result = await api.SaveLabelsAsync(SetId, new() { LabeledBy = LabeledBy.Trim(), Labels = chosen.Select(r => new HrLabelInput
                { ApplicationId = r.Candidate.ApplicationId, IsPass = r.Choice == "pass", Note = r.Note }).ToList() }, ct);
            if (!result.IsSuccess) { Error = result.ErrorMessage; return false; }
            chosen.ForEach(r => r.MarkSaved());
            Message = $"Đã lưu nhãn HR: {result.Data!.LabelCount} hồ sơ · {result.Data.PassCount} Đạt · {result.Data.LabelCount - result.Data.PassCount} Không đạt."
                + (result.Data.PassCount == 0 || result.Data.PassCount == result.Data.LabelCount ? " Cần đủ cả hai loại nhãn trước khi chạy." : " Chọn bộ này ở phần Chạy đánh giá.");
            var list = await api.GetEvaluationSetsAsync(ct);
            if (list.IsSuccess) Sets = list.Data!;
            else Error = "Nhãn đã lưu nhưng chưa tải lại được thống kê. Hãy kiểm tra lại danh sách.";
            // Keep all typed choices and notes even when refreshing statistics fails.
            return true;
        }
        finally { Busy = false; }
    }
}
