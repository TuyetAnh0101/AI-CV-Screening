using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RejectReason",
                table: "Applications",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "Applications",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "New");

            migrationBuilder.AddColumn<DateTime>(
                name: "StageUpdatedAt",
                table: "Applications",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StageVersion",
                table: "Applications",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.CreateTable(
                name: "ApplicationEvents",
                columns: table => new
                {
                    EventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApplicationId = table.Column<int>(type: "int", nullable: false),
                    FromStage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ToStage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RejectReason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationEvents", x => x.EventId);
                    table.CheckConstraint("CK_ApplicationEvents_Actor", "LEN(LTRIM(RTRIM([Actor]))) > 0");
                    table.CheckConstraint("CK_ApplicationEvents_FromStage", "[FromStage] COLLATE Latin1_General_100_BIN2 IN (N'New', N'Reviewing', N'Shortlisted', N'Interview', N'Rejected', N'OnHold')");
                    table.CheckConstraint("CK_ApplicationEvents_RejectReason", "[RejectReason] IS NULL OR [RejectReason] COLLATE Latin1_General_100_BIN2 IN (N'MissingSkills', N'NotEnoughExperience', N'OtherRequirement', N'CandidateWithdrew', N'PositionFilled', N'Duplicate')");
                    table.CheckConstraint("CK_ApplicationEvents_RejectReason_Stage", "([ToStage] = N'Rejected' AND [RejectReason] IS NOT NULL) OR ([ToStage] <> N'Rejected' AND [RejectReason] IS NULL)");
                    table.CheckConstraint("CK_ApplicationEvents_ToStage", "[ToStage] COLLATE Latin1_General_100_BIN2 IN (N'New', N'Reviewing', N'Shortlisted', N'Interview', N'Rejected', N'OnHold')");
                    table.ForeignKey(
                        name: "FK_ApplicationEvents_Applications",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "ApplicationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_Job_Stage",
                table: "Applications",
                columns: new[] { "JobId", "Stage" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Applications_RejectReason",
                table: "Applications",
                sql: "[RejectReason] IS NULL OR [RejectReason] COLLATE Latin1_General_100_BIN2 IN (N'MissingSkills', N'NotEnoughExperience', N'OtherRequirement', N'CandidateWithdrew', N'PositionFilled', N'Duplicate')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Applications_RejectReason_Stage",
                table: "Applications",
                sql: "([Stage] = N'Rejected' AND [RejectReason] IS NOT NULL) OR ([Stage] <> N'Rejected' AND [RejectReason] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Applications_Stage",
                table: "Applications",
                sql: "[Stage] COLLATE Latin1_General_100_BIN2 IN (N'New', N'Reviewing', N'Shortlisted', N'Interview', N'Rejected', N'OnHold')");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationEvents_Application_CreatedAt",
                table: "ApplicationEvents",
                columns: new[] { "ApplicationId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER [dbo].[TR_ApplicationEvents_NoChange]
                ON [dbo].[ApplicationEvents]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''Lịch sử quyết định chỉ được thêm, không được sửa hoặc xóa.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_ApplicationEvents_NoChange];");
            migrationBuilder.DropTable(
                name: "ApplicationEvents");

            migrationBuilder.DropIndex(
                name: "IX_Applications_Job_Stage",
                table: "Applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Applications_RejectReason",
                table: "Applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Applications_RejectReason_Stage",
                table: "Applications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Applications_Stage",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "RejectReason",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "StageUpdatedAt",
                table: "Applications");

            migrationBuilder.DropColumn(
                name: "StageVersion",
                table: "Applications");
        }
    }
}
