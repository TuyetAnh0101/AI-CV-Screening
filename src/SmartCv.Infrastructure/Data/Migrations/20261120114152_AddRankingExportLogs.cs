using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRankingExportLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExportLogs",
                columns: table => new
                {
                    ExportId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AuditEventId = table.Column<long>(type: "bigint", nullable: false),
                    RunId = table.Column<int>(type: "int", nullable: false),
                    JobId = table.Column<int>(type: "int", nullable: false),
                    Format = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    Stage = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Fit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    SearchApplied = table.Column<bool>(type: "bit", nullable: false),
                    NeedsReview = table.Column<bool>(type: "bit", nullable: false),
                    ManualFlags = table.Column<bool>(type: "bit", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    Anonymized = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportLogs", x => x.ExportId);
                    table.CheckConstraint("CK_ExportLogs_Fit", "[Fit] COLLATE Latin1_General_100_BIN2 IN ('all','pass','notpass','unscored','excellent','good','potential','poor','issue')");
                    table.CheckConstraint("CK_ExportLogs_Format", "[Format] COLLATE Latin1_General_100_BIN2 = 'xlsx'");
                    table.CheckConstraint("CK_ExportLogs_Rows", "[RowCount] > 0 AND [RowCount] <= 5000");
                    table.CheckConstraint("CK_ExportLogs_Stage", "[Stage] COLLATE Latin1_General_100_BIN2 IN ('all','New','Reviewing','Shortlisted','Interview','Rejected','OnHold')");
                    table.ForeignKey(
                        name: "FK_ExportLogs_AuditEvents_AuditEventId",
                        column: x => x.AuditEventId,
                        principalTable: "AuditEvents",
                        principalColumn: "AuditEventId");
                    table.ForeignKey(
                        name: "FK_ExportLogs_EvaluationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "EvaluationRuns",
                        principalColumn: "RunId");
                    table.ForeignKey(
                        name: "FK_ExportLogs_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "JobId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExportLogs_AuditEventId",
                table: "ExportLogs",
                column: "AuditEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExportLogs_JobId_ExportId",
                table: "ExportLogs",
                columns: new[] { "JobId", "ExportId" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ExportLogs_RunId_ExportId",
                table: "ExportLogs",
                columns: new[] { "RunId", "ExportId" },
                descending: new[] { false, true });

            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER [dbo].[TR_ExportLogs_NoChange]
                ON [dbo].[ExportLogs] AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51031, N''Export logs are append-only.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExportLogs");
        }
    }
}
