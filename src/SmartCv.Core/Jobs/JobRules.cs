using SmartCv.Core.Contracts;

namespace SmartCv.Core.Jobs;

public static class JobRules
{
    public const int TitleMax = 150;
    public const int DescriptionMax = 20_000;
    public const decimal MaxYears = 50;
    public const int MaxHeadcount = 1_000;

    public static Dictionary<string, string[]> Validate(JobUpsertRequest request, DateOnly todayVn, bool isCreate)
    {
        var errors = new Dictionary<string, string[]>();
        var title = NormalizeTitle(request.Title);
        if (title.Length == 0) errors["title"] = ["Nhập tên vị trí tuyển dụng."];
        else if (title.Length > TitleMax) errors["title"] = [$"Tên tối đa {TitleMax} ký tự."];
        if (request.Description is { Length: > DescriptionMax }) errors["description"] = ["Mô tả tối đa 20.000 ký tự."];
        if (request.MinGpa is < 0 or > 4) errors["minGpa"] = ["GPA tối thiểu phải từ 0 đến 4."];
        if (request.MinYearsExperience is < 0 or > MaxYears) errors["minYearsExperience"] = ["Số năm kinh nghiệm phải từ 0 đến 50."];
        if (request.Headcount is < 1 or > MaxHeadcount) errors["headcount"] = ["Số lượng cần tuyển phải từ 1 đến 1.000."];
        if (isCreate && request.ClosingDate < todayVn) errors["closingDate"] = ["Hạn nhận hồ sơ không được trước hôm nay theo giờ Việt Nam."];
        return errors;
    }

    public static string NormalizeTitle(string? title) =>
        string.Join(' ', (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool IsExpired(DateOnly? closingDate, DateOnly todayVn) => closingDate < todayVn;
}
