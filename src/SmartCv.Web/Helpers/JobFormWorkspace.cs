using System.ComponentModel.DataAnnotations;
using SmartCv.Core.Contracts;
using SmartCv.Core.Jobs;
using SmartCv.Web.Models;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

/// <summary>B21 draft and confirmed server snapshot are separate. No write is retried automatically.</summary>
public sealed class JobFormWorkspace(JobApiClient api, int? id) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Guid> metadataSaved = [], aliasesSaved = [];
    private bool generalSaved, disposed;
    public int? Id { get; private set; } = id;
    public JobFormModel Model { get; private set; } = new();
    public JobDto? Original { get; private set; }
    public bool Loading { get; private set; } = true;
    public bool Busy { get; private set; }
    public bool NeedsVerification { get; private set; }
    public bool InputTouched { get; set; }
    public bool NotFound { get; private set; }
    public string? LoadError { get; private set; }
    public string? Notice { get; private set; }
    public bool NoticeIsError { get; private set; }
    public Dictionary<string, string[]> FieldErrors { get; } = [];
    public bool IsNew => Id is null;
    public bool CanWrite => IsNew || Original?.HasEvaluations is not null;
    public bool Locked => Busy || Loading || NeedsVerification || !CanWrite;
    public int PendingCount => (Original is null ? 0 : (SkillDiff.GeneralChanged(Original, Model) ? 1 : 0) + SkillDiff.Compute(Original.Skills, Model.Skills).Count)
        + Model.Skills.Count(s => s.DeleteBlocked && s.Error is not null || s.MissingOnServer);
    public bool IsDirty => InputTouched || NeedsVerification || PendingCount > 0 || Model.Skills.Any(s => s.AliasDraft.Length > 0)
        || IsNew && (Model.Title.Length > 0 || Model.Description?.Length > 0 || Model.Skills.Count > 0 || Model.MinGpa is not null || Model.Headcount is not null || Model.ClosingDate is not null || Model.MinYearsExperience != 0);

    public async Task LoadAsync()
    {
        if (disposed || Busy) return;
        Loading = true; LoadError = null; NotFound = false;
        try
        {
            if (Id is null) return;
            if (Id <= 0) { NotFound = true; LoadError = "Mã vị trí phải là số nguyên dương."; return; }
            var response = await api.GetJobAsync(Id.Value, lifetime.Token);
            if (disposed) return;
            if (!response.IsSuccess) { NotFound = response.StatusCode == 404; LoadError = Failure(response); return; }
            if (!Usable(response.Data, Id)) { LoadError = "Máy chủ trả về dữ liệu vị trí không hợp lệ. Hãy thử tải lại."; return; }
            Original = response.Data!; Model = JobFormModel.From(Original); InputTouched = false;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { Loading = false; }
    }

    public async Task<int?> SaveAsync()
    {
        if (disposed || Locked) return null;
        var validation = new List<ValidationResult>();
        if (!Validator.TryValidateObject(Model, new(Model), validation, true)) { Notice = "Kiểm tra các trường được đánh dấu trước khi lưu."; NoticeIsError = true; return null; }
        Busy = true; Notice = null; FieldErrors.Clear(); NoticeIsError = false;
        try
        {
            if (IsNew)
            {
                var result = await api.CreateAsync(Model.ToRequest(true), lifetime.Token);
                if (disposed) return null;
                if (result.IsSuccess && Usable(result.Data))
                {
                    Id = result.Data!.JobId; Original = result.Data; Model = JobFormModel.From(Original); InputTouched = false;
                    Notice = "Đã tạo vị trí tuyển dụng"; return Id;
                }
                ApplyGeneralFailure(result);
                NeedsVerification = Uncertain(result) || result.IsSuccess;
                if (NeedsVerification) Notice = "Chưa xác nhận được việc tạo vị trí. Kiểm tra trạng thái trước khi gửi thêm yêu cầu.";
                return null;
            }
            var ops = SkillDiff.Compute(Original!.Skills, Model.Skills);
            var generalChanged = SkillDiff.GeneralChanged(Original, Model);
            if (!generalChanged && ops.Count == 0)
            {
                InputTouched = false;
                foreach (var row in Model.Skills.Where(s => !s.Deleted)) { row.SetAliases(row.NormalizeAliases().Aliases); }
                Notice = PendingCount == 0 ? "Không có thay đổi cần lưu." : $"Còn {PendingCount} thay đổi chưa lưu được. Xem dòng được đánh dấu.";
                NoticeIsError = PendingCount > 0; return null;
            }
            metadataSaved.Clear(); aliasesSaved.Clear(); generalSaved = false;
            if (generalChanged)
            {
                var r = await api.UpdateAsync(Id!.Value, Model.ToRequest(false), lifetime.Token);
                if (disposed) return null;
                if (r.IsSuccess && Usable(r.Data, Id)) { generalSaved = true; Model.SetGeneral(r.Data!); }
                else { ApplyGeneralFailure(r); if (Uncertain(r) || r.IsSuccess) { NeedsVerification = true; return null; } }
            }
            foreach (var op in ops)
            {
                var row = op.Row;
                if (op.Kind == SkillOpKind.Delete)
                {
                    var r = await api.DeleteSkillAsync(Id!.Value, op.Code, lifetime.Token);
                    if (disposed) return null;
                    if (r.IsSuccess) Model.Skills.Remove(row);
                    else
                    {
                        row.Error = Failure(r);
                        if (r.StatusCode == 409 && r.ValidationErrors.Count == 0)
                        {
                            row.Deleted = false; row.DeleteBlocked = true;
                            row.Error = "Không thể xóa kỹ năng để bảo toàn lịch sử đánh giá. Có thể sửa tên, loại hoặc từ khóa nếu được phép.";
                        }
                        if (Uncertain(r)) { NeedsVerification = true; break; }
                    }
                    continue;
                }
                ApiResult<JobSkillDto> response = op.Kind switch
                {
                    SkillOpKind.Add => await api.AddSkillAsync(Id!.Value, row.ToInput(), lifetime.Token),
                    SkillOpKind.Update => await api.UpdateSkillAsync(Id!.Value, op.Code, new() { DisplayName = row.DisplayName.Trim(), SkillType = row.SkillType }, lifetime.Token),
                    _ => await api.ReplaceAliasesAsync(Id!.Value, op.Code, row.NormalizeAliases().Aliases, lifetime.Token)
                };
                if (disposed) return null;
                if (response.IsSuccess && UsableSkill(response.Data) && string.Equals(response.Data!.SkillCode, op.Code, StringComparison.OrdinalIgnoreCase))
                {
                    var data = response.Data!;
                    if (op.Kind is SkillOpKind.Add or SkillOpKind.Update)
                    {
                        metadataSaved.Add(row.Key); row.DisplayName = data.DisplayName; row.SkillType = data.SkillType;
                        row.IsSaved = true; row.SkillCode = row.SavedCode = data.SkillCode;
                    }
                    if (op.Kind is SkillOpKind.Add or SkillOpKind.ReplaceAliases) { aliasesSaved.Add(row.Key); row.SetAliases(data.Aliases); }
                    if (!row.DeleteBlocked) row.Error = null;
                }
                else
                {
                    row.Error = Failure(response);
                    foreach (var (field, errors) in response.ValidationErrors) row.Validation[field] = string.Join(" ", errors);
                    if (Uncertain(response) || response.IsSuccess) { NeedsVerification = true; break; }
                }
            }
            if (!NeedsVerification) await ReadAndReconcileAsync();
            InputTouched = false;
            if (!NeedsVerification) SetOutcome();
            return null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
        finally
        {
            if (NeedsVerification) { Notice = "Chưa xác nhận được toàn bộ thay đổi. Bản nháp vẫn được giữ; tải lại trạng thái trước khi lưu tiếp."; NoticeIsError = true; }
            Busy = false;
        }
    }

    public async Task<int?> VerifyAsync()
    {
        if (disposed || Busy || !NeedsVerification) return null;
        Busy = true;
        try
        {
            if (IsNew)
            {
                var list = await api.GetJobsAsync(false, lifetime.Token);
                if (disposed) return null;
                if (!list.IsSuccess || list.Data is null || !list.Data.All(j => Usable(j))) { Notice = "Chưa kiểm tra được trạng thái. Bản nháp vẫn được giữ và chưa gửi lại yêu cầu."; return null; }
                var matches = list.Data!.Where(j => string.Equals(JobRules.NormalizeTitle(j.Title), JobRules.NormalizeTitle(Model.Title), StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0) { NeedsVerification = false; Notice = "Chưa thấy vị trí cùng tên trên máy chủ. Hãy kiểm tra trước khi chủ động bấm Tạo vị trí lần nữa."; return null; }
                if (matches.Count == 1 && !SkillDiff.GeneralChanged(matches[0], Model) && SameSkills(matches[0], Model))
                {
                    Id = matches[0].JobId; Original = matches[0]; Model = JobFormModel.From(Original); NeedsVerification = false; InputTouched = false;
                    Notice = "Đã xác minh vị trí tuyển dụng trên máy chủ."; return Id;
                }
                Notice = "Đã có vị trí cùng tên nhưng tiêu chí khác bản nháp. Mở danh sách để đối chiếu; chưa gửi lại yêu cầu tạo."; return null;
            }
            await ReadAndReconcileAsync();
            if (!NeedsVerification) SetOutcome(verified: true);
            return null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
        finally { Busy = false; }
    }

    private async Task ReadAndReconcileAsync()
    {
        var response = await api.GetJobAsync(Id!.Value, lifetime.Token);
        if (disposed) return;
        if (!response.IsSuccess || !Usable(response.Data, Id)) { NeedsVerification = true; return; }
        var fresh = response.Data!;
        var old = Original!;
        if (generalSaved || !SkillDiff.GeneralChanged(old, Model) || !SkillDiff.GeneralChanged(fresh, Model)) Model.SetGeneral(fresh);
        foreach (var row in Model.Skills.ToList())
        {
            var current = fresh.Skills.FirstOrDefault(s => string.Equals(s.SkillCode, row.EffectiveCode, StringComparison.OrdinalIgnoreCase));
            var before = old.Skills.FirstOrDefault(s => string.Equals(s.SkillCode, row.SavedCode ?? row.EffectiveCode, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                if (!row.IsSaved) continue;
                if (row.Deleted) Model.Skills.Remove(row);
                else { row.MissingOnServer = true; row.Error = "Kỹ năng không còn trên máy chủ. Nội dung bản nháp được giữ để đối chiếu."; }
                continue;
            }
            // A lost Add response can be reconciled only when the actual saved fields all match.
            var verifiedAdd = !row.IsSaved && current.DisplayName == row.DisplayName.Trim() && current.SkillType == row.SkillType && SkillDiff.AliasesEqual(current.Aliases, row.NormalizeAliases().Aliases);
            if (!row.IsSaved && !verifiedAdd) continue;
            if (verifiedAdd || metadataSaved.Contains(row.Key) || before is not null && before.DisplayName == row.DisplayName.Trim() && before.SkillType == row.SkillType)
            { row.DisplayName = current.DisplayName; row.SkillType = current.SkillType; }
            if (verifiedAdd || aliasesSaved.Contains(row.Key) || before is not null && SkillDiff.AliasesEqual(before.Aliases, row.NormalizeAliases().Aliases)) row.SetAliases(current.Aliases);
            row.IsSaved = true; row.SkillCode = row.SavedCode = current.SkillCode; row.MissingOnServer = false;
        }
        foreach (var serverRow in fresh.Skills.Where(s => !Model.Skills.Any(r => r.IsSaved && string.Equals(r.SkillCode, s.SkillCode, StringComparison.OrdinalIgnoreCase)))) Model.Skills.Add(SkillFormModel.From(serverRow));
        Original = fresh; NeedsVerification = false; generalSaved = false; metadataSaved.Clear(); aliasesSaved.Clear();
    }
    private static bool SameSkills(JobDto job, JobFormModel model) => job.Skills.Count == model.Skills.Count(s => !s.Deleted) && model.Skills.Where(s => !s.Deleted).All(r => job.Skills.Any(s => string.Equals(s.SkillCode, r.EffectiveCode, StringComparison.OrdinalIgnoreCase) && s.DisplayName == r.DisplayName.Trim() && s.SkillType == r.SkillType && SkillDiff.AliasesEqual(s.Aliases, r.NormalizeAliases().Aliases)));
    private void SetOutcome(bool verified = false)
    {
        var remaining = PendingCount;
        NoticeIsError = remaining > 0 || FieldErrors.Count > 0;
        Notice = NoticeIsError ? $"Đã đối chiếu phần đã lưu; còn {Math.Max(remaining, 1)} thay đổi chưa lưu được. Xem trường và dòng được đánh dấu."
            : verified ? "Đã tải lại và xác minh trạng thái trên máy chủ." : "Đã lưu thay đổi.";
    }
    private static bool UsableSkill(JobSkillDto? skill) => skill is not null && !string.IsNullOrWhiteSpace(skill.SkillCode) && !string.IsNullOrWhiteSpace(skill.DisplayName)
        && skill.SkillType is "MustHave" or "NiceToHave" && skill.Aliases is not null && skill.Aliases.All(a => a is not null);
    private static bool Usable(JobDto? job, int? expected = null) => job is { JobId: > 0 } && (expected is null || job.JobId == expected) && !string.IsNullOrWhiteSpace(job.Title)
        && job.Skills is not null && job.Skills.All(UsableSkill) && job.Skills.Select(s => s.SkillCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == job.Skills.Count;
    private void ApplyGeneralFailure<T>(ApiResult<T> result)
    {
        NoticeIsError = true; Notice = Failure(result);
        foreach (var (field, errors) in result.ValidationErrors)
        {
            if (field.StartsWith("skills[", StringComparison.Ordinal) && field.IndexOf(']') is var close && close > 7 && int.TryParse(field.AsSpan(7, close - 7), out var index))
            {
                var active = Model.Skills.Where(s => !s.Deleted).ToList();
                if (index >= 0 && index < active.Count) { var row = active[index]; row.Error = string.Join(" ", errors); row.Validation[field[(close + 1)..].TrimStart('.')] = row.Error; }
            }
            else FieldErrors[field] = errors;
        }
        if (result.StatusCode == 409 && FieldErrors.Count == 0 && !Model.Skills.Any(s => s.Error is not null)) FieldErrors["title"] = ["Tên vị trí đã được sử dụng. Hãy chọn tên khác."];
    }
    private static bool Uncertain<T>(ApiResult<T> r) => !r.IsSuccess && (r.StatusCode is null or >= 500 or 408 || r.StatusCode is >= 200 and < 300);
    private static string Failure<T>(ApiResult<T> r) => r.StatusCode switch
    {
        null => "Không nhận được phản hồi hợp lệ từ máy chủ. Kiểm tra kết nối và trạng thái trước khi lưu tiếp.",
        401 or 403 => "Bạn chưa có quyền thay đổi vị trí tuyển dụng.",
        404 => "Vị trí hoặc thao tác không khả dụng. Kiểm tra quyền truy cập nội bộ và tải lại.",
        429 => "Có quá nhiều yêu cầu. Vui lòng chờ rồi chủ động thử lại.",
        >= 500 => "Máy chủ tạm thời chưa xử lý được yêu cầu. Bản nháp được giữ lại.",
        _ => r.ErrorMessage ?? "Chưa lưu được thay đổi. Hãy kiểm tra dữ liệu."
    };
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
}
