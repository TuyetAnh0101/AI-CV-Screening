using System.Text.Json.Serialization;

namespace SmartCv.Core.Contracts;

/// <summary>General Job information. Skills/aliases are accepted on POST only; PUT leaves them intact.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class JobUpsertRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public decimal? MinGpa { get; set; }
    public decimal MinYearsExperience { get; set; }
    public int? Headcount { get; set; }
    public DateOnly? ClosingDate { get; set; }
    public List<JobSkillInput>? Skills { get; set; }
}
