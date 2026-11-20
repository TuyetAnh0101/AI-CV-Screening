namespace SmartCv.Core.Options;

public sealed class ExportOptions
{
    public const string SectionName = "Export";
    public int MaxRows { get; set; } = 5000;
}
