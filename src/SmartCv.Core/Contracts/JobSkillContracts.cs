using System.Text.Json.Serialization;

namespace SmartCv.Core.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class JobSkillInput
{
    public string? SkillCode { get; set; }
    public string? DisplayName { get; set; }
    public string? SkillType { get; set; }
    public List<string?>? Aliases { get; set; }
}

// Deliberately excludes SkillCode: keys referenced by saved evaluations are immutable.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UpdateJobSkillRequest
{
    public string? DisplayName { get; set; }
    public string? SkillType { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ReplaceAliasesRequest
{
    // Omitted/null is invalid; an explicit [] clears all aliases.
    public List<string?>? Aliases { get; set; }
}
