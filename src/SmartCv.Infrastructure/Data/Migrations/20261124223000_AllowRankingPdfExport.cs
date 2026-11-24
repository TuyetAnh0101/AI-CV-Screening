using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmartCv.Infrastructure.Data.Migrations;

/// <summary>
/// Adds PDF to the existing append-only ExportLogs allowlist.
/// No rows, scores, CVs, workspaces or prior audit events are changed.
/// </summary>
[DbContext(typeof(SmartCvDbContext))]
[Migration("20261124223000_AllowRankingPdfExport")]
public sealed class AllowRankingPdfExport : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "CK_ExportLogs_Format", table: "ExportLogs");
        migrationBuilder.AddCheckConstraint(
            name: "CK_ExportLogs_Format", table: "ExportLogs",
            sql: "[Format] COLLATE Latin1_General_100_BIN2 IN ('xlsx','pdf')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Prevent rolling back the format rule while actual PDF audit rows exist.
        migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM dbo.ExportLogs WHERE [Format] = 'pdf') THROW 51032, 'ExportLogs contains PDF events; cannot downgrade schema.', 1;");
        migrationBuilder.DropCheckConstraint(name: "CK_ExportLogs_Format", table: "ExportLogs");
        migrationBuilder.AddCheckConstraint(
            name: "CK_ExportLogs_Format", table: "ExportLogs",
            sql: "[Format] COLLATE Latin1_General_100_BIN2 = 'xlsx'");
    }
}
