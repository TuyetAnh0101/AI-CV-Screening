using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeCandidateDataRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "CandidateDataRequests",
                type: "int",
                nullable: true);

            // Never guess that an orphan from a multi-workspace database belongs to Internal.
            // Live candidates, retained CV IDs and trusted audit records supply ownership evidence.
            migrationBuilder.Sql("""
                UPDATE r SET WorkspaceId = c.WorkspaceId
                FROM CandidateDataRequests r JOIN Candidates c ON c.CandidateId = r.CandidateId;
                UPDATE r SET WorkspaceId = evidence.WorkspaceId
                FROM CandidateDataRequests r
                CROSS APPLY (
                    SELECT MIN(c.WorkspaceId) WorkspaceId
                    FROM Cvs c
                    WHERE CHARINDEX(',' + LOWER(CONVERT(varchar(36), c.CvId)) + ',', ',' + LOWER(COALESCE(r.CvIds, '')) + ',') > 0
                    HAVING COUNT(DISTINCT c.WorkspaceId) = 1
                ) evidence
                WHERE r.WorkspaceId IS NULL AND evidence.WorkspaceId IS NOT NULL;
                UPDATE r SET WorkspaceId = evidence.WorkspaceId
                FROM CandidateDataRequests r
                CROSS APPLY (
                    SELECT MIN(a.WorkspaceId) WorkspaceId FROM AuditEvents a
                    WHERE a.ResourceType = N'Candidate' AND a.ResourceId = CONVERT(nvarchar(80), r.CandidateId)
                      AND a.ActionCode = N'candidates.erased' AND a.Outcome = N'Succeeded'
                    HAVING COUNT(DISTINCT a.WorkspaceId) = 1
                ) evidence
                WHERE r.WorkspaceId IS NULL AND evidence.WorkspaceId IS NOT NULL;
                IF NOT EXISTS (SELECT 1 FROM Workspaces WHERE WorkspaceId <> 1)
                    UPDATE CandidateDataRequests SET WorkspaceId = 1 WHERE WorkspaceId IS NULL;
                IF EXISTS (SELECT 1 FROM CandidateDataRequests WHERE WorkspaceId IS NULL)
                    THROW 51053, N'A53: unresolved journal workspace ownership; review orphan records before applying this migration.', 1;
                """);
            migrationBuilder.AlterColumn<int>(name: "WorkspaceId", table: "CandidateDataRequests", type: "int", nullable: false,
                oldClrType: typeof(int), oldType: "int", oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateDataRequests_Workspace_Candidate_CreatedAt",
                table: "CandidateDataRequests",
                columns: new[] { "WorkspaceId", "CandidateId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_CandidateDataRequests_Workspaces_WorkspaceId",
                table: "CandidateDataRequests",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CandidateDataRequests_Workspaces_WorkspaceId",
                table: "CandidateDataRequests");

            migrationBuilder.DropIndex(
                name: "IX_CandidateDataRequests_Workspace_Candidate_CreatedAt",
                table: "CandidateDataRequests");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "CandidateDataRequests");
        }
    }
}
