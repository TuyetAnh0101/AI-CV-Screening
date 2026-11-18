using SmartCv.Core.Contracts;
using SmartCv.Core.Jobs;
using SmartCv.Web.Models;

namespace SmartCv.Web.Helpers;

public enum SkillOpKind { Delete, Add, Update, ReplaceAliases }
public sealed record SkillOp(SkillOpKind Kind, string Code, SkillFormModel Row);
public static class SkillDiff
{
    public static bool AliasesEqual(IEnumerable<string> a, IEnumerable<string> b) => JobSkillRules.NormalizeAliases(a).Aliases.Order(StringComparer.Ordinal)
        .SequenceEqual(JobSkillRules.NormalizeAliases(b).Aliases.Order(StringComparer.Ordinal));
    public static List<SkillOp> Compute(IReadOnlyList<JobSkillDto> saved, IReadOnlyList<SkillFormModel> rows)
    {
        var ops = new List<SkillOp>();
        foreach (var row in rows)
        {
            if (row.MissingOnServer) continue;
            var old = saved.FirstOrDefault(s => string.Equals(s.SkillCode, row.IsSaved ? row.SavedCode : row.EffectiveCode, StringComparison.OrdinalIgnoreCase));
            if (row.Deleted) { if (old is not null && !row.DeleteBlocked) ops.Add(new(SkillOpKind.Delete, old.SkillCode, row)); continue; }
            if (!row.IsSaved) { ops.Add(new(SkillOpKind.Add, row.EffectiveCode, row)); continue; }
            if (old is null) continue;
            if (old.DisplayName != row.DisplayName.Trim() || old.SkillType != row.SkillType) ops.Add(new(SkillOpKind.Update, old.SkillCode, row));
            if (!AliasesEqual(old.Aliases, row.NormalizeAliases().Aliases)) ops.Add(new(SkillOpKind.ReplaceAliases, old.SkillCode, row));
        }
        return ops.OrderBy(o => o.Kind).ToList();
    }
    public static bool GeneralChanged(JobDto old, JobFormModel model)
    {
        var r = model.ToRequest(false);
        return old.Title != r.Title || old.Description != r.Description || old.Headcount != r.Headcount || old.ClosingDate != r.ClosingDate
            || old.MinGpa != (r.MinGpa is { } gpa ? Math.Round(gpa, 2, MidpointRounding.AwayFromZero) : null)
            || old.MinYearsExperience != Math.Round(r.MinYearsExperience, 1, MidpointRounding.AwayFromZero);
    }
}
