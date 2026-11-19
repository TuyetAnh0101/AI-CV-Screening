using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScappWorkflowV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JobId",
                table: "EvaluationSets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "EvaluationSets",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "EvaluationSets",
                type: "int",
                nullable: false,
                defaultValue: 1)
                .Annotation("Relational:DefaultConstraintName", "DF_EvaluationSets_Revision");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "EvaluationSets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EvaluationSetRevision",
                table: "EvaluationRuns",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApplicationFlags",
                columns: table => new
                {
                    FlagId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_ApplicationFlags_CreatedAt"),
                    ResolvedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolutionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationFlags", x => x.FlagId);
                    table.CheckConstraint("CK_ApplicationFlags_CreatedBy", "LEN(LTRIM(RTRIM([CreatedBy]))) > 0");
                    table.CheckConstraint("CK_ApplicationFlags_Reason", "[Reason] COLLATE Latin1_General_100_BIN2 IN (N'ScoreDoubt', N'ExtractionIssue', N'MissingInformation', N'SecondOpinion', N'VerifyRequirement', N'Other')");
                    table.CheckConstraint("CK_ApplicationFlags_Resolved", "([Status] = N'Open' AND [ResolvedAt] IS NULL AND [ResolvedBy] IS NULL) OR ([Status] = N'Resolved' AND [ResolvedAt] IS NOT NULL AND [ResolvedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_ApplicationFlags_Status", "[Status] COLLATE Latin1_General_100_BIN2 IN (N'Open', N'Resolved')");
                    table.ForeignKey(
                        name: "FK_ApplicationFlags_Applications",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "ApplicationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationSetMembers",
                columns: table => new
                {
                    EvaluationSetId = table.Column<int>(type: "int", nullable: false),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    AddedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AddedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_EvaluationSetMembers_AddedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationSetMembers", x => new { x.EvaluationSetId, x.ApplicationId });
                    table.ForeignKey(
                        name: "FK_EvaluationSetMembers_Applications",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "ApplicationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvaluationSetMembers_EvaluationSets",
                        column: x => x.EvaluationSetId,
                        principalTable: "EvaluationSets",
                        principalColumn: "EvaluationSetId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationFlagEvents",
                columns: table => new
                {
                    EventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlagId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_ApplicationFlagEvents_CreatedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationFlagEvents", x => x.EventId);
                    table.CheckConstraint("CK_ApplicationFlagEvents_Action", "[Action] COLLATE Latin1_General_100_BIN2 IN (N'Opened', N'Resolved', N'Reopened')");
                    table.CheckConstraint("CK_ApplicationFlagEvents_Actor", "LEN(LTRIM(RTRIM([Actor]))) > 0");
                    table.ForeignKey(
                        name: "FK_ApplicationFlagEvents_ApplicationFlags",
                        column: x => x.FlagId,
                        principalTable: "ApplicationFlags",
                        principalColumn: "FlagId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSets_JobId",
                table: "EvaluationSets",
                column: "JobId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EvaluationSets_Revision",
                table: "EvaluationSets",
                sql: "[Revision] >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFlagEvents_Flag_CreatedAt",
                table: "ApplicationFlagEvents",
                columns: new[] { "FlagId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationFlags_Application_CreatedAt",
                table: "ApplicationFlags",
                columns: new[] { "ApplicationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_ApplicationFlags_OpenPerApplication",
                table: "ApplicationFlags",
                column: "ApplicationId",
                unique: true,
                filter: "[Status] = N'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSetMembers_ApplicationId",
                table: "EvaluationSetMembers",
                column: "ApplicationId");

            migrationBuilder.AddForeignKey(
                name: "FK_EvaluationSets_Jobs",
                table: "EvaluationSets",
                column: "JobId",
                principalTable: "Jobs",
                principalColumn: "JobId",
                onDelete: ReferentialAction.SetNull);

            // Bảo toàn bộ cũ: trước v2, "có nhãn HR" chính là "thuộc bộ". Mỗi nhãn hiện có trở thành một thành viên,
            // giữ người và thời điểm gán nhãn. Không sửa HrLabels, EvaluationRuns hay Evaluations.
            migrationBuilder.Sql("""
                INSERT INTO dbo.EvaluationSetMembers (EvaluationSetId, ApplicationId, AddedBy, AddedAt)
                SELECT h.EvaluationSetId, h.ApplicationId, h.LabeledBy, h.LabeledAt
                FROM dbo.HrLabels h
                WHERE NOT EXISTS (SELECT 1 FROM dbo.EvaluationSetMembers m
                                  WHERE m.EvaluationSetId = h.EvaluationSetId AND m.ApplicationId = h.ApplicationId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EvaluationSets_Jobs",
                table: "EvaluationSets");

            migrationBuilder.DropTable(
                name: "ApplicationFlagEvents");

            migrationBuilder.DropTable(
                name: "EvaluationSetMembers");

            migrationBuilder.DropTable(
                name: "ApplicationFlags");

            migrationBuilder.DropIndex(
                name: "IX_EvaluationSets_JobId",
                table: "EvaluationSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EvaluationSets_Revision",
                table: "EvaluationSets");

            migrationBuilder.DropColumn(
                name: "JobId",
                table: "EvaluationSets");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "EvaluationSets");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "EvaluationSets")
                .Annotation("Relational:DefaultConstraintName", "DF_EvaluationSets_Revision");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "EvaluationSets");

            migrationBuilder.DropColumn(
                name: "EvaluationSetRevision",
                table: "EvaluationRuns");
        }
    }
}
