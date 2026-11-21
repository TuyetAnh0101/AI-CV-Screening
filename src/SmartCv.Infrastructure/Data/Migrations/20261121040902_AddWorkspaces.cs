using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <summary>
    /// A51: workspace. Chuyển đổi không reset (guide mục 6.5): mọi dữ liệu cũ thuộc workspace nội bộ WorkspaceId = 1,
    /// mọi tài khoản cũ là thành viên workspace 1. Không sửa dòng Users nào ngoài việc điền các cột MỚI
    /// (EmailVerifiedAt = CreatedAt, SignupSource = 'Admin'); Email, PasswordHash, Role, IsActive, SecurityStamp giữ nguyên.
    /// Seed của SeedJobs không bị xóa / chèn lại: JobSeedData chỉ khai báo thêm WorkspaceId = 1, còn dòng thật được backfill bằng UPDATE.
    /// Mọi câu lệnh DML tham chiếu cột mới đều đặt trong EXEC(N'...') để script --idempotent không lỗi biên dịch lô.
    /// </summary>
    public partial class AddWorkspaces : Migration
    {
        // Bảng nghiệp vụ có WorkspaceId NOT NULL. ExportLogs xử lý riêng vì có trigger chặn UPDATE.
        private static readonly string[] BackfilledTables =
        {
            "Jobs", "Cvs", "Candidates", "Applications", "PromptVersions", "EvaluationSets", "EvaluationRuns"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- 1. Bỏ khóa ngoại và ràng buộc duy nhất toàn cục cũ (thay bằng bản theo workspace ở bước 6) ----
            migrationBuilder.DropForeignKey(name: "FK_Applications_Cvs", table: "Applications");
            migrationBuilder.DropForeignKey(name: "FK_Applications_Jobs", table: "Applications");
            migrationBuilder.DropForeignKey(name: "FK_Cvs_Candidates", table: "Cvs");
            migrationBuilder.DropForeignKey(name: "FK_EvaluationRuns_EvaluationSets", table: "EvaluationRuns");
            migrationBuilder.DropForeignKey(name: "FK_EvaluationRuns_PromptVersions", table: "EvaluationRuns");
            migrationBuilder.DropForeignKey(name: "FK_EvaluationSets_Jobs", table: "EvaluationSets");

            migrationBuilder.DropUniqueConstraint(name: "UQ_PromptVersions_VersionName", table: "PromptVersions");
            migrationBuilder.DropIndex(name: "UX_PromptVersions_OneActive", table: "PromptVersions");
            migrationBuilder.DropIndex(name: "UQ_Jobs_Title", table: "Jobs");
            migrationBuilder.DropUniqueConstraint(name: "UQ_EvaluationSets_Name", table: "EvaluationSets");
            migrationBuilder.DropUniqueConstraint(name: "UQ_Cvs_FileHash", table: "Cvs");

            // ---- 2. Bảng mới + workspace nội bộ số 1 ----
            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    WorkspaceId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    OwnerUserId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_Workspaces_IsActive"),
                    DailyAiEvaluationLimit = table.Column<int>(type: "int", nullable: true),
                    MaxCvCount = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Workspaces_CreatedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.WorkspaceId);
                    table.CheckConstraint("CK_Workspaces_DailyAiEvaluationLimit", "[DailyAiEvaluationLimit] IS NULL OR [DailyAiEvaluationLimit] >= 0");
                    table.CheckConstraint("CK_Workspaces_Kind", "[Kind] COLLATE Latin1_General_100_BIN2 IN ('Internal', 'Personal')");
                    table.CheckConstraint("CK_Workspaces_MaxCvCount", "[MaxCvCount] IS NULL OR [MaxCvCount] >= 0");
                    table.ForeignKey(
                        name: "FK_Workspaces_Users_Owner",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMembers",
                columns: table => new
                {
                    WorkspaceId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_WorkspaceMembers_JoinedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMembers", x => new { x.WorkspaceId, x.UserId });
                    table.CheckConstraint("CK_WorkspaceMembers_Role", "[Role] COLLATE Latin1_General_100_BIN2 IN ('Owner', 'Member')");
                    table.ForeignKey(
                        name: "FK_WorkspaceMembers_Users",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                    table.ForeignKey(
                        name: "FK_WorkspaceMembers_Workspaces",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "WorkspaceId");
                });

            migrationBuilder.CreateTable(
                name: "UserTokens",
                columns: table => new
                {
                    UserTokenId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Purpose = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    TokenHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_UserTokens_CreatedAt"),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTokens", x => x.UserTokenId);
                    table.CheckConstraint("CK_UserTokens_TokenHash", "LEN([TokenHash]) = 64");
                    table.ForeignKey(
                        name: "FK_UserTokens_Users",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserExternalLogins",
                columns: table => new
                {
                    UserExternalLoginId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EmailAtLink = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    LinkedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_UserExternalLogins_LinkedAt"),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserExternalLogins", x => x.UserExternalLoginId);
                    table.ForeignKey(
                        name: "FK_UserExternalLogins_Users",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            // Workspace nội bộ (EF tự bật IDENTITY_INSERT). OwnerUserId được cập nhật ở bước 4.
            migrationBuilder.InsertData(
                table: "Workspaces",
                columns: new[] { "WorkspaceId", "CreatedAt", "DailyAiEvaluationLimit", "IsActive", "Kind", "MaxCvCount", "Name", "OwnerUserId" },
                values: new object[] { 1, new DateTime(2026, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Internal", null, "SCApp nội bộ", null });

            // ---- 3. Users: cột mới. Tài khoản cũ do Admin tạo => coi như đã xác minh email lúc CreatedAt ----
            migrationBuilder.AddColumn<DateTime>(name: "EmailVerifiedAt", table: "Users", type: "datetime2", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "SignupSource",
                table: "Users",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: false,
                defaultValue: "Admin")
                .Annotation("Relational:DefaultConstraintName", "DF_Users_SignupSource");
            migrationBuilder.AddColumn<DateTime>(name: "TermsAcceptedAt", table: "Users", type: "datetime2", nullable: true);
            migrationBuilder.Sql("EXEC(N'UPDATE [Users] SET [EmailVerifiedAt] = [CreatedAt] WHERE [EmailVerifiedAt] IS NULL');");
            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_SignupSource",
                table: "Users",
                sql: "[SignupSource] COLLATE Latin1_General_100_BIN2 IN (N'Admin', N'Local', N'google', N'linkedin')");

            // ---- 4. Thành viên + chủ sở hữu workspace 1 (Admin có UserId nhỏ nhất; không sửa dòng Users) ----
            migrationBuilder.Sql(@"EXEC(N'
INSERT INTO [WorkspaceMembers] ([WorkspaceId], [UserId], [Role], [JoinedAt])
SELECT 1, u.[UserId],
       CASE WHEN u.[UserId] = (SELECT MIN([UserId]) FROM [Users] WHERE [Role] = N''Admin'') THEN ''Owner'' ELSE ''Member'' END,
       SYSUTCDATETIME()
FROM [Users] u
WHERE NOT EXISTS (SELECT 1 FROM [WorkspaceMembers] m WHERE m.[WorkspaceId] = 1 AND m.[UserId] = u.[UserId]);

UPDATE [Workspaces] SET [OwnerUserId] = (SELECT MIN([UserId]) FROM [Users] WHERE [Role] = N''Admin'') WHERE [WorkspaceId] = 1;');");

            // ---- 5. WorkspaceId: nullable tạm thời -> backfill = 1 -> NOT NULL ----
            foreach (var table in BackfilledTables)
            {
                migrationBuilder.AddColumn<int>(name: "WorkspaceId", table: table, type: "int", nullable: true);
                migrationBuilder.Sql($"EXEC(N'UPDATE [{table}] SET [WorkspaceId] = 1 WHERE [WorkspaceId] IS NULL');");
                migrationBuilder.AlterColumn<int>(name: "WorkspaceId", table: table, type: "int", nullable: false, oldClrType: typeof(int), oldType: "int", oldNullable: true);
            }

            // ExportLogs có trigger TR_ExportLogs_NoChange (chặn UPDATE) nên không backfill bằng UPDATE:
            // thêm cột NOT NULL kèm DEFAULT tạm (DDL, không kích hoạt trigger) rồi bỏ DEFAULT.
            migrationBuilder.AddColumn<int>(name: "WorkspaceId", table: "ExportLogs", type: "int", nullable: false, defaultValue: 1)
                .Annotation("Relational:DefaultConstraintName", "DF_ExportLogs_WorkspaceId_Tmp");
            migrationBuilder.Sql("EXEC(N'ALTER TABLE [ExportLogs] DROP CONSTRAINT [DF_ExportLogs_WorkspaceId_Tmp]');");

            // AuditEvents: nullable, giữ NULL cho lịch sử (bảng chỉ thêm, trigger chặn UPDATE).
            migrationBuilder.AddColumn<int>(name: "WorkspaceId", table: "AuditEvents", type: "int", nullable: true);

            // ---- 6. Ràng buộc duy nhất / khóa thay thế / chỉ mục / khóa ngoại theo workspace ----
            migrationBuilder.AddUniqueConstraint(name: "AK_PromptVersions_Workspace_Prompt", table: "PromptVersions", columns: new[] { "WorkspaceId", "PromptVersionId" });
            migrationBuilder.AddUniqueConstraint(name: "UQ_PromptVersions_Workspace_VersionName", table: "PromptVersions", columns: new[] { "WorkspaceId", "VersionName" });
            migrationBuilder.AddUniqueConstraint(name: "AK_Jobs_Workspace_Job", table: "Jobs", columns: new[] { "WorkspaceId", "JobId" });
            migrationBuilder.AddUniqueConstraint(name: "AK_EvaluationSets_Workspace_Set", table: "EvaluationSets", columns: new[] { "WorkspaceId", "EvaluationSetId" });
            migrationBuilder.AddUniqueConstraint(name: "UQ_EvaluationSets_Workspace_Name", table: "EvaluationSets", columns: new[] { "WorkspaceId", "Name" });
            migrationBuilder.AddUniqueConstraint(name: "AK_Cvs_Workspace_Cv", table: "Cvs", columns: new[] { "WorkspaceId", "CvId" });
            migrationBuilder.AddUniqueConstraint(name: "AK_Candidates_Workspace_Candidate", table: "Candidates", columns: new[] { "WorkspaceId", "CandidateId" });

            migrationBuilder.CreateIndex(name: "UX_PromptVersions_Workspace_OneActive", table: "PromptVersions", column: "WorkspaceId", unique: true, filter: "[IsActive] = 1");
            migrationBuilder.CreateIndex(name: "UQ_Jobs_Workspace_Title", table: "Jobs", columns: new[] { "WorkspaceId", "Title" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_ExportLogs_Workspace_Export", table: "ExportLogs", columns: new[] { "WorkspaceId", "ExportId" }, descending: new[] { false, true });
            migrationBuilder.CreateIndex(name: "IX_EvaluationSets_WorkspaceId_JobId", table: "EvaluationSets", columns: new[] { "WorkspaceId", "JobId" });
            migrationBuilder.CreateIndex(name: "IX_EvaluationRuns_WorkspaceId_EvaluationSetId", table: "EvaluationRuns", columns: new[] { "WorkspaceId", "EvaluationSetId" });
            migrationBuilder.CreateIndex(name: "IX_EvaluationRuns_WorkspaceId_PromptVersionId", table: "EvaluationRuns", columns: new[] { "WorkspaceId", "PromptVersionId" });
            migrationBuilder.CreateIndex(name: "IX_Cvs_Workspace_FileHash", table: "Cvs", columns: new[] { "WorkspaceId", "FileHash" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_Cvs_WorkspaceId_CandidateId", table: "Cvs", columns: new[] { "WorkspaceId", "CandidateId" });
            migrationBuilder.CreateIndex(name: "IX_AuditEvents_Workspace_Time", table: "AuditEvents", columns: new[] { "WorkspaceId", "OccurredAtUtc" }, descending: new[] { false, true });
            migrationBuilder.CreateIndex(name: "IX_Applications_WorkspaceId_CvId", table: "Applications", columns: new[] { "WorkspaceId", "CvId" });
            migrationBuilder.CreateIndex(name: "IX_Applications_WorkspaceId_JobId", table: "Applications", columns: new[] { "WorkspaceId", "JobId" });
            migrationBuilder.CreateIndex(name: "UX_UserExternalLogins_Provider_Key", table: "UserExternalLogins", columns: new[] { "Provider", "ProviderKey" }, unique: true);
            migrationBuilder.CreateIndex(name: "UX_UserExternalLogins_User_Provider", table: "UserExternalLogins", columns: new[] { "UserId", "Provider" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_UserTokens_User_Purpose_CreatedAt", table: "UserTokens", columns: new[] { "UserId", "Purpose", "CreatedAt" });
            migrationBuilder.CreateIndex(name: "UX_UserTokens_TokenHash", table: "UserTokens", column: "TokenHash", unique: true);
            migrationBuilder.CreateIndex(name: "IX_WorkspaceMembers_UserId", table: "WorkspaceMembers", column: "UserId");
            migrationBuilder.CreateIndex(name: "UX_WorkspaceMembers_OneOwner", table: "WorkspaceMembers", column: "WorkspaceId", unique: true, filter: "[Role] = 'Owner'");
            migrationBuilder.CreateIndex(name: "UX_Workspaces_PersonalOwner", table: "Workspaces", column: "OwnerUserId", unique: true, filter: "[Kind] = 'Personal'");

            // Khóa ngoại ghép: database từ chối liên kết chéo workspace (giữ hành vi xóa cũ = Restrict).
            migrationBuilder.AddForeignKey(name: "FK_Applications_Cvs", table: "Applications", columns: new[] { "WorkspaceId", "CvId" },
                principalTable: "Cvs", principalColumns: new[] { "WorkspaceId", "CvId" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Applications_Jobs", table: "Applications", columns: new[] { "WorkspaceId", "JobId" },
                principalTable: "Jobs", principalColumns: new[] { "WorkspaceId", "JobId" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Cvs_Candidates", table: "Cvs", columns: new[] { "WorkspaceId", "CandidateId" },
                principalTable: "Candidates", principalColumns: new[] { "WorkspaceId", "CandidateId" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_EvaluationRuns_EvaluationSets", table: "EvaluationRuns", columns: new[] { "WorkspaceId", "EvaluationSetId" },
                principalTable: "EvaluationSets", principalColumns: new[] { "WorkspaceId", "EvaluationSetId" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_EvaluationRuns_PromptVersions", table: "EvaluationRuns", columns: new[] { "WorkspaceId", "PromptVersionId" },
                principalTable: "PromptVersions", principalColumns: new[] { "WorkspaceId", "PromptVersionId" }, onDelete: ReferentialAction.Restrict);
            // SET NULL cũ không dùng được với khóa ghép chứa WorkspaceId NOT NULL; hiện không có chức năng xóa Job.
            migrationBuilder.AddForeignKey(name: "FK_EvaluationSets_Jobs", table: "EvaluationSets", columns: new[] { "WorkspaceId", "JobId" },
                principalTable: "Jobs", principalColumns: new[] { "WorkspaceId", "JobId" }, onDelete: ReferentialAction.Restrict);

            foreach (var table in new[] { "Applications", "AuditEvents", "Candidates", "Cvs", "EvaluationRuns", "EvaluationSets", "ExportLogs", "Jobs", "PromptVersions" })
            {
                migrationBuilder.AddForeignKey(name: $"FK_{table}_Workspaces", table: table, column: "WorkspaceId",
                    principalTable: "Workspaces", principalColumn: "WorkspaceId");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Chỉ dùng trên database thử nghiệm. Với dữ liệu thật hãy khôi phục từ backup, không chạy Down().
            // Down() sẽ lỗi nếu hai workspace đã có Job/CV/Prompt/Bộ trùng tên hoặc hash (ràng buộc toàn cục cũ không còn thỏa).
            foreach (var table in new[] { "Applications", "AuditEvents", "Candidates", "Cvs", "EvaluationRuns", "EvaluationSets", "ExportLogs", "Jobs", "PromptVersions" })
                migrationBuilder.DropForeignKey(name: $"FK_{table}_Workspaces", table: table);

            migrationBuilder.DropForeignKey(name: "FK_Applications_Cvs", table: "Applications");
            migrationBuilder.DropForeignKey(name: "FK_Applications_Jobs", table: "Applications");
            migrationBuilder.DropForeignKey(name: "FK_Cvs_Candidates", table: "Cvs");
            migrationBuilder.DropForeignKey(name: "FK_EvaluationRuns_EvaluationSets", table: "EvaluationRuns");
            migrationBuilder.DropForeignKey(name: "FK_EvaluationRuns_PromptVersions", table: "EvaluationRuns");
            migrationBuilder.DropForeignKey(name: "FK_EvaluationSets_Jobs", table: "EvaluationSets");

            migrationBuilder.DropTable(name: "UserExternalLogins");
            migrationBuilder.DropTable(name: "UserTokens");
            migrationBuilder.DropTable(name: "WorkspaceMembers");
            migrationBuilder.DropTable(name: "Workspaces");

            migrationBuilder.DropCheckConstraint(name: "CK_Users_SignupSource", table: "Users");

            migrationBuilder.DropUniqueConstraint(name: "AK_PromptVersions_Workspace_Prompt", table: "PromptVersions");
            migrationBuilder.DropUniqueConstraint(name: "UQ_PromptVersions_Workspace_VersionName", table: "PromptVersions");
            migrationBuilder.DropIndex(name: "UX_PromptVersions_Workspace_OneActive", table: "PromptVersions");
            migrationBuilder.DropUniqueConstraint(name: "AK_Jobs_Workspace_Job", table: "Jobs");
            migrationBuilder.DropIndex(name: "UQ_Jobs_Workspace_Title", table: "Jobs");
            migrationBuilder.DropIndex(name: "IX_ExportLogs_Workspace_Export", table: "ExportLogs");
            migrationBuilder.DropUniqueConstraint(name: "AK_EvaluationSets_Workspace_Set", table: "EvaluationSets");
            migrationBuilder.DropUniqueConstraint(name: "UQ_EvaluationSets_Workspace_Name", table: "EvaluationSets");
            migrationBuilder.DropIndex(name: "IX_EvaluationSets_WorkspaceId_JobId", table: "EvaluationSets");
            migrationBuilder.DropIndex(name: "IX_EvaluationRuns_WorkspaceId_EvaluationSetId", table: "EvaluationRuns");
            migrationBuilder.DropIndex(name: "IX_EvaluationRuns_WorkspaceId_PromptVersionId", table: "EvaluationRuns");
            migrationBuilder.DropUniqueConstraint(name: "AK_Cvs_Workspace_Cv", table: "Cvs");
            migrationBuilder.DropIndex(name: "IX_Cvs_Workspace_FileHash", table: "Cvs");
            migrationBuilder.DropIndex(name: "IX_Cvs_WorkspaceId_CandidateId", table: "Cvs");
            migrationBuilder.DropUniqueConstraint(name: "AK_Candidates_Workspace_Candidate", table: "Candidates");
            migrationBuilder.DropIndex(name: "IX_AuditEvents_Workspace_Time", table: "AuditEvents");
            migrationBuilder.DropIndex(name: "IX_Applications_WorkspaceId_CvId", table: "Applications");
            migrationBuilder.DropIndex(name: "IX_Applications_WorkspaceId_JobId", table: "Applications");

            migrationBuilder.DropColumn(name: "EmailVerifiedAt", table: "Users");
            migrationBuilder.DropColumn(name: "SignupSource", table: "Users");
            migrationBuilder.DropColumn(name: "TermsAcceptedAt", table: "Users");
            foreach (var table in new[] { "PromptVersions", "Jobs", "ExportLogs", "EvaluationSets", "EvaluationRuns", "Cvs", "Candidates", "AuditEvents", "Applications" })
                migrationBuilder.DropColumn(name: "WorkspaceId", table: table);

            migrationBuilder.AddUniqueConstraint(name: "UQ_PromptVersions_VersionName", table: "PromptVersions", column: "VersionName");
            migrationBuilder.AddUniqueConstraint(name: "UQ_EvaluationSets_Name", table: "EvaluationSets", column: "Name");
            migrationBuilder.AddUniqueConstraint(name: "UQ_Cvs_FileHash", table: "Cvs", column: "FileHash");
            migrationBuilder.CreateIndex(name: "UX_PromptVersions_OneActive", table: "PromptVersions", column: "IsActive", unique: true, filter: "[IsActive] = 1");
            migrationBuilder.CreateIndex(name: "UQ_Jobs_Title", table: "Jobs", column: "Title", unique: true);

            migrationBuilder.AddForeignKey(name: "FK_Applications_Cvs", table: "Applications", column: "CvId",
                principalTable: "Cvs", principalColumn: "CvId", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Applications_Jobs", table: "Applications", column: "JobId",
                principalTable: "Jobs", principalColumn: "JobId", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Cvs_Candidates", table: "Cvs", column: "CandidateId",
                principalTable: "Candidates", principalColumn: "CandidateId", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_EvaluationRuns_EvaluationSets", table: "EvaluationRuns", column: "EvaluationSetId",
                principalTable: "EvaluationSets", principalColumn: "EvaluationSetId", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_EvaluationRuns_PromptVersions", table: "EvaluationRuns", column: "PromptVersionId",
                principalTable: "PromptVersions", principalColumn: "PromptVersionId", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_EvaluationSets_Jobs", table: "EvaluationSets", column: "JobId",
                principalTable: "Jobs", principalColumn: "JobId", onDelete: ReferentialAction.SetNull);
        }
    }
}
