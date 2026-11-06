using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Candidates",
                columns: table => new
                {
                    CandidateId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FullName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    PhoneNumber = table.Column<string>(type: "char(10)", unicode: false, fixedLength: true, maxLength: 10, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Candidates_CreatedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Candidates", x => x.CandidateId);
                    table.CheckConstraint("CK_Candidates_PhoneNumber", "PhoneNumber NOT LIKE '%[^0-9]%'");
                });

            migrationBuilder.CreateTable(
                name: "EvaluationSets",
                columns: table => new
                {
                    EvaluationSetId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_EvaluationSets_CreatedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationSets", x => x.EvaluationSetId);
                    table.UniqueConstraint("UQ_EvaluationSets_Name", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    JobId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MinGpa = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: true),
                    MinYearsExperience = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: false, defaultValue: 0m)
                        .Annotation("Relational:DefaultConstraintName", "DF_Jobs_MinYearsExperience"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                        .Annotation("Relational:DefaultConstraintName", "DF_Jobs_IsActive"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Jobs_CreatedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.JobId);
                    table.UniqueConstraint("UQ_Jobs_Title", x => x.Title);
                    table.CheckConstraint("CK_Jobs_MinGpa", "MinGpa BETWEEN 0 AND 4");
                    table.CheckConstraint("CK_Jobs_MinYearsExperience", "MinYearsExperience >= 0");
                });

            migrationBuilder.CreateTable(
                name: "PromptVersions",
                columns: table => new
                {
                    PromptVersionId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VersionName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                        .Annotation("Relational:DefaultConstraintName", "DF_PromptVersions_IsActive"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_PromptVersions_CreatedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptVersions", x => x.PromptVersionId);
                    table.UniqueConstraint("UQ_PromptVersions_VersionName", x => x.VersionName);
                });

            migrationBuilder.CreateTable(
                name: "Cvs",
                columns: table => new
                {
                    CvId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CandidateId = table.Column<int>(type: "int", nullable: true),
                    OriginalFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    StoredPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FileHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ExtractedText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExtractionMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ExtractionStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Pending")
                        .Annotation("Relational:DefaultConstraintName", "DF_Cvs_ExtractionStatus"),
                    ExtractionError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Cvs_UploadedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cvs", x => x.CvId);
                    table.UniqueConstraint("UQ_Cvs_FileHash", x => x.FileHash);
                    table.CheckConstraint("CK_Cvs_ExtractionMethod", "ExtractionMethod IN (N'PdfText', N'PdfOcr', N'ImageOcr')");
                    table.CheckConstraint("CK_Cvs_ExtractionStatus", "ExtractionStatus IN (N'Pending', N'Completed', N'Failed')");
                    table.CheckConstraint("CK_Cvs_FileSizeBytes", "FileSizeBytes > 0");
                    table.ForeignKey(
                        name: "FK_Cvs_Candidates",
                        column: x => x.CandidateId,
                        principalTable: "Candidates",
                        principalColumn: "CandidateId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobSkills",
                columns: table => new
                {
                    JobId = table.Column<int>(type: "int", nullable: false),
                    SkillCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SkillType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSkills", x => new { x.JobId, x.SkillCode });
                    table.CheckConstraint("CK_JobSkills_SkillType", "SkillType IN (N'MustHave', N'NiceToHave')");
                    table.ForeignKey(
                        name: "FK_JobSkills_Jobs",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "JobId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationRuns",
                columns: table => new
                {
                    RunId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PromptVersionId = table.Column<int>(type: "int", nullable: false),
                    EvaluationSetId = table.Column<int>(type: "int", nullable: true),
                    ModelName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Temperature = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: false),
                    KeywordWeight = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: false),
                    AiWeight = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: false),
                    PassThreshold = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false, defaultValue: 70m)
                        .Annotation("Relational:DefaultConstraintName", "DF_EvaluationRuns_PassThreshold"),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Queued")
                        .Annotation("Relational:DefaultConstraintName", "DF_EvaluationRuns_Status"),
                    TotalItems = table.Column<int>(type: "int", nullable: false),
                    ProcessedItems = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                        .Annotation("Relational:DefaultConstraintName", "DF_EvaluationRuns_ProcessedItems"),
                    FailedItems = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                        .Annotation("Relational:DefaultConstraintName", "DF_EvaluationRuns_FailedItems"),
                    AccuracyPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_EvaluationRuns_CreatedAt"),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationRuns", x => x.RunId);
                    table.CheckConstraint("CK_EvaluationRuns_AccuracyPercent", "AccuracyPercent BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_EvaluationRuns_AiWeight", "AiWeight BETWEEN 0 AND 1");
                    table.CheckConstraint("CK_EvaluationRuns_FailedItems", "FailedItems >= 0");
                    table.CheckConstraint("CK_EvaluationRuns_KeywordWeight", "KeywordWeight BETWEEN 0 AND 1");
                    table.CheckConstraint("CK_EvaluationRuns_PassThreshold", "PassThreshold BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_EvaluationRuns_ProcessedItems", "ProcessedItems >= 0");
                    table.CheckConstraint("CK_EvaluationRuns_RunType", "RunType IN (N'Screening', N'PromptTest')");
                    table.CheckConstraint("CK_EvaluationRuns_Set", "(RunType = N'PromptTest' AND EvaluationSetId IS NOT NULL) OR (RunType = N'Screening' AND EvaluationSetId IS NULL)");
                    table.CheckConstraint("CK_EvaluationRuns_Status", "Status IN (N'Queued', N'Running', N'Completed', N'Failed')");
                    table.CheckConstraint("CK_EvaluationRuns_Temperature", "Temperature >= 0");
                    table.CheckConstraint("CK_EvaluationRuns_TotalItems", "TotalItems >= 0");
                    table.CheckConstraint("CK_EvaluationRuns_Weights", "KeywordWeight + AiWeight = 1");
                    table.ForeignKey(
                        name: "FK_EvaluationRuns_EvaluationSets",
                        column: x => x.EvaluationSetId,
                        principalTable: "EvaluationSets",
                        principalColumn: "EvaluationSetId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvaluationRuns_PromptVersions",
                        column: x => x.PromptVersionId,
                        principalTable: "PromptVersions",
                        principalColumn: "PromptVersionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    ApplicationId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CvId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Applications_CreatedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.ApplicationId);
                    table.UniqueConstraint("UQ_Applications_Cv_Job", x => new { x.CvId, x.JobId });
                    table.ForeignKey(
                        name: "FK_Applications_Cvs",
                        column: x => x.CvId,
                        principalTable: "Cvs",
                        principalColumn: "CvId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Applications_Jobs",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "JobId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobSkillAliases",
                columns: table => new
                {
                    JobId = table.Column<int>(type: "int", nullable: false),
                    SkillCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSkillAliases", x => new { x.JobId, x.SkillCode, x.Alias });
                    table.ForeignKey(
                        name: "FK_JobSkillAliases_JobSkills",
                        columns: x => new { x.JobId, x.SkillCode },
                        principalTable: "JobSkills",
                        principalColumns: new[] { "JobId", "SkillCode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Evaluations",
                columns: table => new
                {
                    EvaluationId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<int>(type: "int", nullable: false),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    KeywordScore = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    AiScore = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    FinalScore = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    ScoreSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FitLabel = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    PredictedPass = table.Column<bool>(type: "bit", nullable: true),
                    YearsExperience = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: true),
                    Reasoning = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AiRawResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Pending")
                        .Annotation("Relational:DefaultConstraintName", "DF_Evaluations_Status"),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_Evaluations_CreatedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Evaluations", x => x.EvaluationId);
                    table.UniqueConstraint("UQ_Evaluations_Run_Application", x => new { x.RunId, x.ApplicationId });
                    table.CheckConstraint("CK_Evaluations_AiScore", "AiScore BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_Evaluations_FinalScore", "FinalScore BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_Evaluations_KeywordScore", "KeywordScore BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_Evaluations_ScoreSource", "ScoreSource IN (N'Combined', N'KeywordOnly')");
                    table.CheckConstraint("CK_Evaluations_Status", "Status IN (N'Pending', N'Completed', N'Failed')");
                    table.CheckConstraint("CK_Evaluations_YearsExperience", "YearsExperience >= 0");
                    table.ForeignKey(
                        name: "FK_Evaluations_Applications",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "ApplicationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Evaluations_EvaluationRuns",
                        column: x => x.RunId,
                        principalTable: "EvaluationRuns",
                        principalColumn: "RunId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HrLabels",
                columns: table => new
                {
                    EvaluationSetId = table.Column<int>(type: "int", nullable: false),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    IsPass = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LabeledBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LabeledAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_HrLabels_LabeledAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HrLabels", x => new { x.EvaluationSetId, x.ApplicationId });
                    table.ForeignKey(
                        name: "FK_HrLabels_Applications",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "ApplicationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HrLabels_EvaluationSets",
                        column: x => x.EvaluationSetId,
                        principalTable: "EvaluationSets",
                        principalColumn: "EvaluationSetId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationSkills",
                columns: table => new
                {
                    EvaluationId = table.Column<int>(type: "int", nullable: false),
                    JobId = table.Column<int>(type: "int", nullable: false),
                    SkillCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsMatched = table.Column<bool>(type: "bit", nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationSkills", x => new { x.EvaluationId, x.JobId, x.SkillCode });
                    table.ForeignKey(
                        name: "FK_EvaluationSkills_Evaluations",
                        column: x => x.EvaluationId,
                        principalTable: "Evaluations",
                        principalColumn: "EvaluationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvaluationSkills_JobSkills",
                        columns: x => new { x.JobId, x.SkillCode },
                        principalTable: "JobSkills",
                        principalColumns: new[] { "JobId", "SkillCode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_JobId",
                table: "Applications",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Cvs_CandidateId",
                table: "Cvs",
                column: "CandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationRuns_EvaluationSetId",
                table: "EvaluationRuns",
                column: "EvaluationSetId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationRuns_PromptVersionId",
                table: "EvaluationRuns",
                column: "PromptVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Evaluations_ApplicationId",
                table: "Evaluations",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_Evaluations_Run_FinalScore",
                table: "Evaluations",
                columns: new[] { "RunId", "FinalScore" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSkills_JobSkill",
                table: "EvaluationSkills",
                columns: new[] { "JobId", "SkillCode" });

            migrationBuilder.CreateIndex(
                name: "IX_HrLabels_ApplicationId",
                table: "HrLabels",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "UX_PromptVersions_OneActive",
                table: "PromptVersions",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvaluationSkills");

            migrationBuilder.DropTable(
                name: "HrLabels");

            migrationBuilder.DropTable(
                name: "JobSkillAliases");

            migrationBuilder.DropTable(
                name: "Evaluations");

            migrationBuilder.DropTable(
                name: "JobSkills");

            migrationBuilder.DropTable(
                name: "Applications");

            migrationBuilder.DropTable(
                name: "EvaluationRuns");

            migrationBuilder.DropTable(
                name: "Cvs");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "EvaluationSets");

            migrationBuilder.DropTable(
                name: "PromptVersions");

            migrationBuilder.DropTable(
                name: "Candidates");
        }
    }
}
