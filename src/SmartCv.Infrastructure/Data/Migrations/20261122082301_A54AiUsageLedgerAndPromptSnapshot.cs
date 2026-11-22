using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class A54AiUsageLedgerAndPromptSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SystemPromptSnapshot",
                table: "EvaluationRuns",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiUsageEntries",
                columns: table => new
                {
                    AiUsageEntryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkspaceId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RunId = table.Column<int>(type: "int", nullable: true),
                    Units = table.Column<int>(type: "int", nullable: false),
                    UnitsReleased = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageEntries", x => x.AiUsageEntryId);
                    table.CheckConstraint("CK_AiUsageEntries_Run", "([Source] = N'Preview' AND [RunId] IS NULL) OR ([Source] <> N'Preview' AND [RunId] IS NOT NULL)");
                    table.CheckConstraint("CK_AiUsageEntries_Source", "[Source] IN (N'Screening', N'PromptTest', N'Preview')");
                    table.CheckConstraint("CK_AiUsageEntries_Units", "[Units] > 0 AND [UnitsReleased] BETWEEN 0 AND [Units]");
                    table.ForeignKey(
                        name: "FK_AiUsageEntries_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "WorkspaceId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageEntries_WorkspaceId_CreatedAtUtc",
                table: "AiUsageEntries",
                columns: new[] { "WorkspaceId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageEntries_WorkspaceId_RunId",
                table: "AiUsageEntries",
                columns: new[] { "WorkspaceId", "RunId" },
                unique: true,
                filter: "[RunId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsageEntries");

            migrationBuilder.DropColumn(
                name: "SystemPromptSnapshot",
                table: "EvaluationRuns");
        }
    }
}
