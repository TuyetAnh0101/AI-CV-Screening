namespace SmartCv.Api.Ops;

/// <summary>Authoritative business validations for changes to personal workspace capacity.</summary>
public static class AdminQuotaRules
{
    public static readonly string[] Reasons = ["Capacity", "Trial", "Incident", "Correction"];

    public static string? Validate(int dailyAi, int maxCvs, string? reason)
    {
        if (dailyAi is < 0 or > 10000)
            return "Hạn mức AI/ngày phải từ 0 đến 10.000. Giá trị 0 ngăn lượt chấm mới.";
        if (maxCvs is < 0 or > 100000)
            return "Hạn mức CV phải từ 0 đến 100.000. Giá trị 0 ngăn tải CV mới.";
        if (reason is null || !Reasons.Contains(reason, StringComparer.Ordinal))
            return "Lý do chỉ nhận: Capacity, Trial, Incident hoặc Correction.";
        return null;
    }
}
