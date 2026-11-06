using SmartCv.Core.Constants;

namespace SmartCv.Infrastructure.Data.Seed;

/// <summary>
/// Dữ liệu nền A3: 5 job, kỹ năng và từ khóa tương đương.
/// Được nạp vào database qua migration (HasData), nên ai chạy "dotnet ef database update" cũng có sẵn.
/// LƯU Ý: chỉ dùng giá trị cố định. Không dùng DateTime.UtcNow, Guid.NewGuid()...
/// vì mỗi lần tạo migration giá trị đổi, EF sẽ tưởng dữ liệu thay đổi và sinh migration rác.
/// </summary>
public static class JobSeedData
{
    private static readonly DateTime SeedDate = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    // Dùng object ẩn danh (new { ... }) thay vì new Job { ... } để không dính navigation property.
    public static readonly object[] Jobs =
    {
        new { JobId = 1, Title = "Backend Developer (.NET)", Description = "Phát triển Web API bằng ASP.NET Core, làm việc với SQL Server và Entity Framework Core.", MinGpa = (decimal?)2.50m, MinYearsExperience = 0.5m, IsActive = true, CreatedAt = SeedDate },
        new { JobId = 2, Title = "Frontend Developer (Blazor)", Description = "Xây dựng giao diện Blazor, kết nối API, đảm bảo giao diện thân thiện và responsive.", MinGpa = (decimal?)2.50m, MinYearsExperience = 0.0m, IsActive = true, CreatedAt = SeedDate },
        new { JobId = 3, Title = "AI Engineer Intern", Description = "Tích hợp mô hình ngôn ngữ lớn (Gemini), viết prompt và đánh giá chất lượng đầu ra.", MinGpa = (decimal?)3.00m, MinYearsExperience = 0.0m, IsActive = true, CreatedAt = SeedDate },
        new { JobId = 4, Title = "Tester (QA)", Description = "Thiết kế test case, kiểm thử chức năng và API, ghi nhận và theo dõi lỗi.", MinGpa = (decimal?)null, MinYearsExperience = 0.0m, IsActive = true, CreatedAt = SeedDate },
        new { JobId = 5, Title = "Business Analyst", Description = "Thu thập, phân tích yêu cầu, mô hình hóa nghiệp vụ và làm cầu nối giữa khách hàng với đội phát triển.", MinGpa = (decimal?)2.80m, MinYearsExperience = 1.0m, IsActive = true, CreatedAt = SeedDate },
    };

    public static readonly object[] JobSkills =
    {
        // Job 1
        new { JobId = 1, SkillCode = "csharp", DisplayName = "C#", SkillType = SkillTypes.MustHave },
        new { JobId = 1, SkillCode = "aspnetcore", DisplayName = "ASP.NET Core", SkillType = SkillTypes.MustHave },
        new { JobId = 1, SkillCode = "sqlserver", DisplayName = "SQL Server", SkillType = SkillTypes.MustHave },
        new { JobId = 1, SkillCode = "efcore", DisplayName = "Entity Framework Core", SkillType = SkillTypes.NiceToHave },
        new { JobId = 1, SkillCode = "git", DisplayName = "Git", SkillType = SkillTypes.NiceToHave },
        new { JobId = 1, SkillCode = "docker", DisplayName = "Docker", SkillType = SkillTypes.NiceToHave },

        // Job 2
        new { JobId = 2, SkillCode = "blazor", DisplayName = "Blazor", SkillType = SkillTypes.MustHave },
        new { JobId = 2, SkillCode = "html_css", DisplayName = "HTML/CSS", SkillType = SkillTypes.MustHave },
        new { JobId = 2, SkillCode = "csharp", DisplayName = "C#", SkillType = SkillTypes.MustHave },
        new { JobId = 2, SkillCode = "bootstrap", DisplayName = "Bootstrap", SkillType = SkillTypes.NiceToHave },
        new { JobId = 2, SkillCode = "figma", DisplayName = "Figma", SkillType = SkillTypes.NiceToHave },
        new { JobId = 2, SkillCode = "javascript", DisplayName = "JavaScript", SkillType = SkillTypes.NiceToHave },

        // Job 3
        new { JobId = 3, SkillCode = "python", DisplayName = "Python", SkillType = SkillTypes.MustHave },
        new { JobId = 3, SkillCode = "machine_learning", DisplayName = "Machine Learning", SkillType = SkillTypes.MustHave },
        new { JobId = 3, SkillCode = "llm_api", DisplayName = "LLM API", SkillType = SkillTypes.MustHave },
        new { JobId = 3, SkillCode = "prompt_engineering", DisplayName = "Prompt Engineering", SkillType = SkillTypes.NiceToHave },
        new { JobId = 3, SkillCode = "sql", DisplayName = "SQL", SkillType = SkillTypes.NiceToHave },
        new { JobId = 3, SkillCode = "nlp", DisplayName = "NLP", SkillType = SkillTypes.NiceToHave },

        // Job 4
        new { JobId = 4, SkillCode = "manual_testing", DisplayName = "Kiểm thử thủ công", SkillType = SkillTypes.MustHave },
        new { JobId = 4, SkillCode = "bug_tracking", DisplayName = "Quản lý lỗi", SkillType = SkillTypes.MustHave },
        new { JobId = 4, SkillCode = "sql", DisplayName = "SQL", SkillType = SkillTypes.MustHave },
        new { JobId = 4, SkillCode = "automation_testing", DisplayName = "Kiểm thử tự động", SkillType = SkillTypes.NiceToHave },
        new { JobId = 4, SkillCode = "api_testing", DisplayName = "Kiểm thử API", SkillType = SkillTypes.NiceToHave },

        // Job 5
        new { JobId = 5, SkillCode = "requirement_analysis", DisplayName = "Phân tích yêu cầu", SkillType = SkillTypes.MustHave },
        new { JobId = 5, SkillCode = "uml", DisplayName = "UML", SkillType = SkillTypes.MustHave },
        new { JobId = 5, SkillCode = "communication", DisplayName = "Giao tiếp", SkillType = SkillTypes.MustHave },
        new { JobId = 5, SkillCode = "sql", DisplayName = "SQL", SkillType = SkillTypes.NiceToHave },
        new { JobId = 5, SkillCode = "figma", DisplayName = "Figma", SkillType = SkillTypes.NiceToHave },
        new { JobId = 5, SkillCode = "english", DisplayName = "Tiếng Anh", SkillType = SkillTypes.NiceToHave },
    };

    public static readonly object[] JobSkillAliases =
    {
        // Job 1
        new { JobId = 1, SkillCode = "csharp", Alias = "C Sharp" },
        new { JobId = 1, SkillCode = "csharp", Alias = "CSharp" },
        new { JobId = 1, SkillCode = "aspnetcore", Alias = "ASP.NET" },
        new { JobId = 1, SkillCode = "aspnetcore", Alias = "Web API" },
        new { JobId = 1, SkillCode = "sqlserver", Alias = "MSSQL" },
        new { JobId = 1, SkillCode = "sqlserver", Alias = "T-SQL" },
        new { JobId = 1, SkillCode = "efcore", Alias = "EF Core" },
        new { JobId = 1, SkillCode = "efcore", Alias = "Entity Framework" },
        new { JobId = 1, SkillCode = "git", Alias = "GitHub" },
        new { JobId = 1, SkillCode = "git", Alias = "GitLab" },
        new { JobId = 1, SkillCode = "docker", Alias = "Container" },

        // Job 2
        new { JobId = 2, SkillCode = "blazor", Alias = "Razor" },
        new { JobId = 2, SkillCode = "blazor", Alias = "Blazor WebAssembly" },
        new { JobId = 2, SkillCode = "html_css", Alias = "HTML5" },
        new { JobId = 2, SkillCode = "html_css", Alias = "CSS3" },
        new { JobId = 2, SkillCode = "csharp", Alias = "C Sharp" },
        new { JobId = 2, SkillCode = "csharp", Alias = "CSharp" },
        new { JobId = 2, SkillCode = "bootstrap", Alias = "Bootstrap 5" },
        new { JobId = 2, SkillCode = "figma", Alias = "UI/UX" },
        new { JobId = 2, SkillCode = "figma", Alias = "Wireframe" },
        new { JobId = 2, SkillCode = "javascript", Alias = "JS" },
        new { JobId = 2, SkillCode = "javascript", Alias = "TypeScript" },

        // Job 3
        new { JobId = 3, SkillCode = "python", Alias = "Python3" },
        new { JobId = 3, SkillCode = "machine_learning", Alias = "ML" },
        new { JobId = 3, SkillCode = "machine_learning", Alias = "Học máy" },
        new { JobId = 3, SkillCode = "llm_api", Alias = "Gemini" },
        new { JobId = 3, SkillCode = "llm_api", Alias = "OpenAI API" },
        new { JobId = 3, SkillCode = "prompt_engineering", Alias = "Prompt" },
        new { JobId = 3, SkillCode = "sql", Alias = "MySQL" },
        new { JobId = 3, SkillCode = "sql", Alias = "SQL Server" },
        new { JobId = 3, SkillCode = "nlp", Alias = "Xử lý ngôn ngữ tự nhiên" },

        // Job 4
        new { JobId = 4, SkillCode = "manual_testing", Alias = "Manual Test" },
        new { JobId = 4, SkillCode = "manual_testing", Alias = "Test case" },
        new { JobId = 4, SkillCode = "bug_tracking", Alias = "Jira" },
        new { JobId = 4, SkillCode = "bug_tracking", Alias = "GitHub Issues" },
        new { JobId = 4, SkillCode = "sql", Alias = "MySQL" },
        new { JobId = 4, SkillCode = "sql", Alias = "SQL Server" },
        new { JobId = 4, SkillCode = "automation_testing", Alias = "Selenium" },
        new { JobId = 4, SkillCode = "automation_testing", Alias = "Playwright" },
        new { JobId = 4, SkillCode = "api_testing", Alias = "Postman" },

        // Job 5
        new { JobId = 5, SkillCode = "requirement_analysis", Alias = "Requirement" },
        new { JobId = 5, SkillCode = "requirement_analysis", Alias = "BRD" },
        new { JobId = 5, SkillCode = "uml", Alias = "Use case" },
        new { JobId = 5, SkillCode = "uml", Alias = "BPMN" },
        new { JobId = 5, SkillCode = "communication", Alias = "Communication" },
        new { JobId = 5, SkillCode = "communication", Alias = "Thuyết trình" },
        new { JobId = 5, SkillCode = "sql", Alias = "MySQL" },
        new { JobId = 5, SkillCode = "sql", Alias = "SQL Server" },
        new { JobId = 5, SkillCode = "figma", Alias = "Wireframe" },
        new { JobId = 5, SkillCode = "figma", Alias = "Mockup" },
        new { JobId = 5, SkillCode = "english", Alias = "IELTS" },
        new { JobId = 5, SkillCode = "english", Alias = "TOEIC" },
    };
}
