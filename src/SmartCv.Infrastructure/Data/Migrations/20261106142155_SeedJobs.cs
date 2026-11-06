using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <summary>
    /// A3: nạp dữ liệu nền 5 Jobs, 29 JobSkills, 52 JobSkillAliases (khai báo bằng HasData trong JobSeedData).
    ///
    /// Thay cho 3 lệnh InsertData do EF sinh ra, migration này dùng SQL có điều kiện để chạy an toàn trên cả:
    ///   - database mới: chèn đủ 86 dòng;
    ///   - database A2 đã có dữ liệu từ seed.sql cũ: dòng đã có và đúng nội dung thì giữ nguyên (kể cả CreatedAt),
    ///     chỉ chèn phần còn thiếu.
    /// Dòng trùng khóa nhưng khác nội dung: migration dừng (THROW), EF ROLLBACK, không ghi đè.
    /// Model snapshot vẫn chứa HasData như bình thường, nên các migration sau tiếp tục so sánh đúng.
    /// Giá trị được ghi cố định tại đây; sửa dữ liệu nền về sau phải tạo migration MỚI, không sửa file này.
    /// </summary>
    public partial class SeedJobs : Migration
    {
        // Dữ liệu nền tại thời điểm tạo migration (khớp JobSeedData). CreatedAt cố định 2026-11-01 (UTC).
        private const string SeedTables = """
            DECLARE @Jobs TABLE (
                JobId int NOT NULL PRIMARY KEY,
                Title nvarchar(150) NOT NULL,
                Description nvarchar(max) NULL,
                MinGpa decimal(3,2) NULL,
                MinYearsExperience decimal(4,1) NOT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2 NOT NULL);
            INSERT INTO @Jobs (JobId, Title, Description, MinGpa, MinYearsExperience, IsActive, CreatedAt) VALUES
            (1, N'Backend Developer (.NET)', N'Phát triển Web API bằng ASP.NET Core, làm việc với SQL Server và Entity Framework Core.', 2.50, 0.5, 1, '2026-11-01T00:00:00'),
            (2, N'Frontend Developer (Blazor)', N'Xây dựng giao diện Blazor, kết nối API, đảm bảo giao diện thân thiện và responsive.', 2.50, 0.0, 1, '2026-11-01T00:00:00'),
            (3, N'AI Engineer Intern', N'Tích hợp mô hình ngôn ngữ lớn (Gemini), viết prompt và đánh giá chất lượng đầu ra.', 3.00, 0.0, 1, '2026-11-01T00:00:00'),
            (4, N'Tester (QA)', N'Thiết kế test case, kiểm thử chức năng và API, ghi nhận và theo dõi lỗi.', NULL, 0.0, 1, '2026-11-01T00:00:00'),
            (5, N'Business Analyst', N'Thu thập, phân tích yêu cầu, mô hình hóa nghiệp vụ và làm cầu nối giữa khách hàng với đội phát triển.', 2.80, 1.0, 1, '2026-11-01T00:00:00');

            DECLARE @Skills TABLE (
                JobId int NOT NULL,
                SkillCode nvarchar(50) NOT NULL,
                DisplayName nvarchar(100) NOT NULL,
                SkillType nvarchar(20) NOT NULL,
                PRIMARY KEY (JobId, SkillCode));
            INSERT INTO @Skills (JobId, SkillCode, DisplayName, SkillType) VALUES
            (1, N'csharp', N'C#', N'MustHave'),
            (1, N'aspnetcore', N'ASP.NET Core', N'MustHave'),
            (1, N'sqlserver', N'SQL Server', N'MustHave'),
            (1, N'efcore', N'Entity Framework Core', N'NiceToHave'),
            (1, N'git', N'Git', N'NiceToHave'),
            (1, N'docker', N'Docker', N'NiceToHave'),
            (2, N'blazor', N'Blazor', N'MustHave'),
            (2, N'html_css', N'HTML/CSS', N'MustHave'),
            (2, N'csharp', N'C#', N'MustHave'),
            (2, N'bootstrap', N'Bootstrap', N'NiceToHave'),
            (2, N'figma', N'Figma', N'NiceToHave'),
            (2, N'javascript', N'JavaScript', N'NiceToHave'),
            (3, N'python', N'Python', N'MustHave'),
            (3, N'machine_learning', N'Machine Learning', N'MustHave'),
            (3, N'llm_api', N'LLM API', N'MustHave'),
            (3, N'prompt_engineering', N'Prompt Engineering', N'NiceToHave'),
            (3, N'sql', N'SQL', N'NiceToHave'),
            (3, N'nlp', N'NLP', N'NiceToHave'),
            (4, N'manual_testing', N'Kiểm thử thủ công', N'MustHave'),
            (4, N'bug_tracking', N'Quản lý lỗi', N'MustHave'),
            (4, N'sql', N'SQL', N'MustHave'),
            (4, N'automation_testing', N'Kiểm thử tự động', N'NiceToHave'),
            (4, N'api_testing', N'Kiểm thử API', N'NiceToHave'),
            (5, N'requirement_analysis', N'Phân tích yêu cầu', N'MustHave'),
            (5, N'uml', N'UML', N'MustHave'),
            (5, N'communication', N'Giao tiếp', N'MustHave'),
            (5, N'sql', N'SQL', N'NiceToHave'),
            (5, N'figma', N'Figma', N'NiceToHave'),
            (5, N'english', N'Tiếng Anh', N'NiceToHave');

            DECLARE @Aliases TABLE (
                JobId int NOT NULL,
                SkillCode nvarchar(50) NOT NULL,
                Alias nvarchar(100) NOT NULL,
                PRIMARY KEY (JobId, SkillCode, Alias));
            INSERT INTO @Aliases (JobId, SkillCode, Alias) VALUES
            (1, N'csharp', N'C Sharp'),
            (1, N'csharp', N'CSharp'),
            (1, N'aspnetcore', N'ASP.NET'),
            (1, N'aspnetcore', N'Web API'),
            (1, N'sqlserver', N'MSSQL'),
            (1, N'sqlserver', N'T-SQL'),
            (1, N'efcore', N'EF Core'),
            (1, N'efcore', N'Entity Framework'),
            (1, N'git', N'GitHub'),
            (1, N'git', N'GitLab'),
            (1, N'docker', N'Container'),
            (2, N'blazor', N'Razor'),
            (2, N'blazor', N'Blazor WebAssembly'),
            (2, N'html_css', N'HTML5'),
            (2, N'html_css', N'CSS3'),
            (2, N'csharp', N'C Sharp'),
            (2, N'csharp', N'CSharp'),
            (2, N'bootstrap', N'Bootstrap 5'),
            (2, N'figma', N'UI/UX'),
            (2, N'figma', N'Wireframe'),
            (2, N'javascript', N'JS'),
            (2, N'javascript', N'TypeScript'),
            (3, N'python', N'Python3'),
            (3, N'machine_learning', N'ML'),
            (3, N'machine_learning', N'Học máy'),
            (3, N'llm_api', N'Gemini'),
            (3, N'llm_api', N'OpenAI API'),
            (3, N'prompt_engineering', N'Prompt'),
            (3, N'sql', N'MySQL'),
            (3, N'sql', N'SQL Server'),
            (3, N'nlp', N'Xử lý ngôn ngữ tự nhiên'),
            (4, N'manual_testing', N'Manual Test'),
            (4, N'manual_testing', N'Test case'),
            (4, N'bug_tracking', N'Jira'),
            (4, N'bug_tracking', N'GitHub Issues'),
            (4, N'sql', N'MySQL'),
            (4, N'sql', N'SQL Server'),
            (4, N'automation_testing', N'Selenium'),
            (4, N'automation_testing', N'Playwright'),
            (4, N'api_testing', N'Postman'),
            (5, N'requirement_analysis', N'Requirement'),
            (5, N'requirement_analysis', N'BRD'),
            (5, N'uml', N'Use case'),
            (5, N'uml', N'BPMN'),
            (5, N'communication', N'Communication'),
            (5, N'communication', N'Thuyết trình'),
            (5, N'sql', N'MySQL'),
            (5, N'sql', N'SQL Server'),
            (5, N'figma', N'Wireframe'),
            (5, N'figma', N'Mockup'),
            (5, N'english', N'IELTS'),
            (5, N'english', N'TOEIC');

            """;

        // Dòng đã có trùng khóa với dữ liệu nền nhưng khác nội dung -> dừng, không ghi đè.
        // So sánh bằng Latin1_General_BIN2 để phân biệt cả chữ hoa/thường và dấu tiếng Việt.
        private const string ConflictChecks = """
            IF EXISTS (
                SELECT 1 FROM dbo.Jobs j JOIN @Jobs s ON s.JobId = j.JobId
                WHERE j.Title COLLATE Latin1_General_BIN2 <> s.Title COLLATE Latin1_General_BIN2
                   OR ISNULL(j.Description, N'') COLLATE Latin1_General_BIN2 <> ISNULL(s.Description, N'') COLLATE Latin1_General_BIN2
                   OR (j.Description IS NULL AND s.Description IS NOT NULL)
                   OR (j.Description IS NOT NULL AND s.Description IS NULL)
                   OR ISNULL(j.MinGpa, -1) <> ISNULL(s.MinGpa, -1)
                   OR j.MinYearsExperience <> s.MinYearsExperience
                   OR j.IsActive <> s.IsActive)
                THROW 50101, N'SeedJobs: bảng Jobs có JobId 1-5 nhưng nội dung khác dữ liệu nền. Không ghi đè, migration dừng lại.', 1;

            IF EXISTS (SELECT 1 FROM dbo.Jobs j JOIN @Jobs s ON s.Title = j.Title AND s.JobId <> j.JobId)
                THROW 50102, N'SeedJobs: tên job của dữ liệu nền đang được dùng bởi một JobId khác (UQ_Jobs_Title). Migration dừng lại.', 1;

            IF EXISTS (
                SELECT 1 FROM dbo.JobSkills k JOIN @Skills s ON s.JobId = k.JobId AND s.SkillCode = k.SkillCode
                WHERE k.SkillCode COLLATE Latin1_General_BIN2 <> s.SkillCode COLLATE Latin1_General_BIN2
                   OR k.DisplayName COLLATE Latin1_General_BIN2 <> s.DisplayName COLLATE Latin1_General_BIN2
                   OR k.SkillType COLLATE Latin1_General_BIN2 <> s.SkillType COLLATE Latin1_General_BIN2)
                THROW 50103, N'SeedJobs: bảng JobSkills có kỹ năng trùng khóa nhưng khác nội dung dữ liệu nền. Migration dừng lại.', 1;

            IF EXISTS (
                SELECT 1 FROM dbo.JobSkillAliases a
                JOIN @Aliases s ON s.JobId = a.JobId AND s.SkillCode = a.SkillCode AND s.Alias = a.Alias
                WHERE a.SkillCode COLLATE Latin1_General_BIN2 <> s.SkillCode COLLATE Latin1_General_BIN2
                   OR a.Alias COLLATE Latin1_General_BIN2 <> s.Alias COLLATE Latin1_General_BIN2)
                THROW 50104, N'SeedJobs: bảng JobSkillAliases có từ khóa trùng khóa nhưng khác chữ hoa/thường hoặc dấu. Migration dừng lại.', 1;

            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SeedTables + ConflictChecks + """
                -- Chỉ chèn phần còn thiếu, theo thứ tự khóa ngoại Jobs -> JobSkills -> JobSkillAliases.
                SET IDENTITY_INSERT dbo.Jobs ON;
                INSERT INTO dbo.Jobs (JobId, Title, Description, MinGpa, MinYearsExperience, IsActive, CreatedAt)
                SELECT s.JobId, s.Title, s.Description, s.MinGpa, s.MinYearsExperience, s.IsActive, s.CreatedAt
                FROM @Jobs s
                WHERE NOT EXISTS (SELECT 1 FROM dbo.Jobs j WHERE j.JobId = s.JobId);
                SET IDENTITY_INSERT dbo.Jobs OFF;

                INSERT INTO dbo.JobSkills (JobId, SkillCode, DisplayName, SkillType)
                SELECT s.JobId, s.SkillCode, s.DisplayName, s.SkillType
                FROM @Skills s
                WHERE NOT EXISTS (SELECT 1 FROM dbo.JobSkills k WHERE k.JobId = s.JobId AND k.SkillCode = s.SkillCode);

                INSERT INTO dbo.JobSkillAliases (JobId, SkillCode, Alias)
                SELECT s.JobId, s.SkillCode, s.Alias
                FROM @Aliases s
                WHERE NOT EXISTS (SELECT 1 FROM dbo.JobSkillAliases a
                                  WHERE a.JobId = s.JobId AND a.SkillCode = s.SkillCode AND a.Alias = s.Alias);

                -- Kiểm tra sau: đủ 5 / 29 / 52 dòng nền.
                IF (SELECT COUNT(*) FROM @Jobs s JOIN dbo.Jobs j ON j.JobId = s.JobId) <> 5
                OR (SELECT COUNT(*) FROM @Skills s JOIN dbo.JobSkills k ON k.JobId = s.JobId AND k.SkillCode = s.SkillCode) <> 29
                OR (SELECT COUNT(*) FROM @Aliases s JOIN dbo.JobSkillAliases a
                        ON a.JobId = s.JobId AND a.SkillCode = s.SkillCode AND a.Alias = s.Alias) <> 52
                    THROW 50105, N'SeedJobs: sau khi nạp không đủ 5 Jobs / 29 JobSkills / 52 JobSkillAliases.', 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Chỉ gỡ dữ liệu nền khi không còn dữ liệu nghiệp vụ dùng tới nó và không có dòng nào bị sửa.
            // Database A2 cũ (có hồ sơ, lượt chấm) bị chặn tại đây: không xóa job đã tồn tại trước migration.
            migrationBuilder.Sql(SeedTables + ConflictChecks + """
                IF EXISTS (SELECT 1 FROM dbo.Applications WHERE JobId IN (SELECT JobId FROM @Jobs))
                OR EXISTS (SELECT 1 FROM dbo.EvaluationSkills WHERE JobId IN (SELECT JobId FROM @Jobs))
                    THROW 50111, N'SeedJobs (Down): dữ liệu nền đang được Applications/EvaluationSkills sử dụng. Không gỡ để tránh mất dữ liệu.', 1;

                IF EXISTS (SELECT 1 FROM dbo.JobSkills k WHERE k.JobId IN (SELECT JobId FROM @Jobs)
                           AND NOT EXISTS (SELECT 1 FROM @Skills s WHERE s.JobId = k.JobId AND s.SkillCode = k.SkillCode))
                OR EXISTS (SELECT 1 FROM dbo.JobSkillAliases a WHERE a.JobId IN (SELECT JobId FROM @Jobs)
                           AND NOT EXISTS (SELECT 1 FROM @Aliases s
                                           WHERE s.JobId = a.JobId AND s.SkillCode = a.SkillCode AND s.Alias = a.Alias))
                    THROW 50112, N'SeedJobs (Down): job nền có kỹ năng/từ khóa ngoài dữ liệu nền. Không gỡ để tránh mất dữ liệu.', 1;

                DELETE a FROM dbo.JobSkillAliases a
                JOIN @Aliases s ON s.JobId = a.JobId AND s.SkillCode = a.SkillCode AND s.Alias = a.Alias;

                DELETE k FROM dbo.JobSkills k
                JOIN @Skills s ON s.JobId = k.JobId AND s.SkillCode = k.SkillCode;

                DELETE j FROM dbo.Jobs j
                JOIN @Jobs s ON s.JobId = j.JobId;
                """);
        }
    }
}
