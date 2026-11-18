using System.ComponentModel.DataAnnotations;
using SmartCv.Core.Common;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Jobs;

namespace SmartCv.Web.Models;

public sealed class JobFormModel : IValidatableObject
{
    public bool IsNew { get; set; } = true;
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public decimal? MinGpa { get; set; }
    public decimal MinYearsExperience { get; set; }
    public int? Headcount { get; set; }
    public DateOnly? ClosingDate { get; set; }
    public List<SkillFormModel> Skills { get; set; } = [];
    public static string Property(string field) => field.Length > 0 ? char.ToUpperInvariant(field[0]) + field[1..] : nameof(Skills);
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        foreach (var (field, errors) in JobRules.Validate(ToRequest(false), VietnamTime.Today(), IsNew))
            foreach (var error in errors) yield return new(error, [Property(field)]);
        var active = Skills.Where(s => !s.Deleted).ToList();
        if (!active.Any(s => s.SkillType == SkillTypes.MustHave)) yield return new("Cần ít nhất một kỹ năng bắt buộc để chấm điểm từ khóa.", [nameof(Skills)]);
        foreach (var s in active)
        {
            s.Validation.Clear();
            foreach (var (field, errors) in s.Validate()) s.Validation[field] = string.Join(" ", errors);
        }
        foreach (var group in active.GroupBy(s => s.EffectiveCode, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var s in group) s.Validation["skillCode"] = "Mã kỹ năng bị trùng trong cùng vị trí.";
        if (active.Any(s => s.Validation.Count > 0)) yield return new("Kiểm tra các ô kỹ năng được đánh dấu bên dưới.", [nameof(Skills)]);
    }
    public JobUpsertRequest ToRequest(bool skills) => new()
    {
        Title = JobRules.NormalizeTitle(Title), Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), MinGpa = MinGpa,
        MinYearsExperience = MinYearsExperience, Headcount = Headcount, ClosingDate = ClosingDate,
        Skills = skills ? Skills.Where(s => !s.Deleted).Select(s => s.ToInput()).ToList() : null
    };
    public static JobFormModel From(JobDto job)
    {
        var model = new JobFormModel { IsNew = false, Skills = job.Skills.Select(SkillFormModel.From).ToList() };
        model.SetGeneral(job); return model;
    }
    public void SetGeneral(JobDto job) { Title = job.Title; Description = job.Description; MinGpa = job.MinGpa; MinYearsExperience = job.MinYearsExperience; Headcount = job.Headcount; ClosingDate = job.ClosingDate; }
}

public sealed class SkillFormModel
{
    public Guid Key { get; } = Guid.NewGuid();
    public string SkillCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SkillType { get; set; } = SkillTypes.MustHave;
    public List<string> Aliases { get; set; } = [];
    public string AliasDraft { get; set; } = "";
    public bool IsSaved { get; set; }
    public string? SavedCode { get; set; }
    public bool CodeEdited { get; set; }
    public bool Deleted { get; set; }
    public bool DeleteBlocked { get; set; }
    public bool MissingOnServer { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, string> Validation { get; } = [];
    public string EffectiveCode => IsSaved ? SkillCode : string.IsNullOrWhiteSpace(SkillCode) ? JobSkillRules.SuggestCode(DisplayName) : JobSkillRules.NormalizeCode(SkillCode);
    public (List<string> Aliases, string? Error) NormalizeAliases() => JobSkillRules.NormalizeAliases(Aliases.Concat(AliasDraft.Split(',')).Cast<string?>());
    public Dictionary<string, string[]> Validate()
    {
        var (_, errors) = JobSkillRules.ValidateNew(ToInput());
        if (IsSaved && !string.Equals(SavedCode, SkillCode, StringComparison.Ordinal)) errors["skillCode"] = ["Mã kỹ năng đã lưu không thể thay đổi."];
        if (NormalizeAliases().Error is { } aliasError) errors["aliases"] = [aliasError];
        if (MissingOnServer) errors["skillCode"] = ["Kỹ năng không còn trên máy chủ. Giữ nội dung để đối chiếu hoặc bỏ dòng này."];
        return errors;
    }
    public JobSkillInput ToInput() => new() { SkillCode = EffectiveCode, DisplayName = DisplayName.Trim(), SkillType = SkillType, Aliases = Aliases.Concat(AliasDraft.Split(',')).Select(a => (string?)a).ToList() };
    public static SkillFormModel From(JobSkillDto skill) => new() { SkillCode = skill.SkillCode, SavedCode = skill.SkillCode, DisplayName = skill.DisplayName, SkillType = skill.SkillType, Aliases = [.. skill.Aliases], IsSaved = true };
    public void SetAliases(IEnumerable<string> aliases) { Aliases = aliases.ToList(); AliasDraft = ""; }
}
