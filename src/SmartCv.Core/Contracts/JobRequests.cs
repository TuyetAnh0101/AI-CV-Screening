using System.Text.Json.Serialization;

namespace SmartCv.Core.Contracts;

/// <summary>A21 general Job information. Skills/aliases belong to A22 and are not accepted here.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class JobUpsertRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public decimal? MinGpa { get; set; }
    public decimal MinYearsExperience { get; set; }
    public int? Headcount { get; set; }
    public DateOnly? ClosingDate { get; set; }
}
