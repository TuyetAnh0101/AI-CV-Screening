namespace SmartCv.Core.Options;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";
    public int Months { get; set; } = 6;
    public bool AutoAnonymize { get; set; } = false;
    public int CheckIntervalHours { get; set; } = 24;
    public int BatchSize { get; set; } = 100;
}
