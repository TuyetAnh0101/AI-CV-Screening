namespace SmartCv.Core.Constants;

// Các giá trị chữ phải khớp đúng với ràng buộc CHECK trong database.
// Dùng hằng số thay vì gõ tay "Pending", "Completed"... để tránh gõ sai chính tả.

public static class SkillTypes
{
    public const string MustHave = "MustHave";
    public const string NiceToHave = "NiceToHave";
}

public static class ExtractionMethods
{
    public const string PdfText = "PdfText";
    public const string PdfOcr = "PdfOcr";
    public const string ImageOcr = "ImageOcr";
}

public static class ExtractionStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class RunTypes
{
    public const string Screening = "Screening";
    public const string PromptTest = "PromptTest";
}

public static class RunStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class EvaluationStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class ScoreSources
{
    public const string Combined = "Combined";
    public const string KeywordOnly = "KeywordOnly";
}
