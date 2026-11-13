namespace SmartCv.Core.Options;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";
    public int Months { get; set; } = 6;
}
