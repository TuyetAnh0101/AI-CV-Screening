using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmartCv.Infrastructure.Data.Migrations;

/// <summary>
/// Append-only operational telemetry queried with parameterized ADO.NET, not an EF domain entity.
/// Therefore it deliberately has no EF model configuration or snapshot entry. This prevents
/// accidentally expanding the ordinary workspace-scoped DbContext query surface.
/// Applied with dotnet ef database update, never by a browser request.
/// </summary>
[DbContext(typeof(SmartCvDbContext))]
[Migration("20261123210000_AddGeminiInvocationLogs")]
public sealed class AddGeminiInvocationLogs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GeminiInvocationLogs",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                WorkspaceId = table.Column<int>(type: "int", nullable: false),
                Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                RunId = table.Column<int>(type: "int", nullable: true),
                EvaluationId = table.Column<int>(type: "int", nullable: true),
                ModelName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                AttemptNumber = table.Column<int>(type: "int", nullable: false),
                OccurredAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                DurationMs = table.Column<int>(type: "int", nullable: false),
                HttpStatus = table.Column<int>(type: "int", nullable: true),
                Outcome = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                PromptTokens = table.Column<int>(type: "int", nullable: true),
                CandidateTokens = table.Column<int>(type: "int", nullable: true),
                ThoughtsTokens = table.Column<int>(type: "int", nullable: true),
                CachedTokens = table.Column<int>(type: "int", nullable: true),
                TotalTokens = table.Column<int>(type: "int", nullable: true),
                EstimatedCostUsd = table.Column<decimal>(type: "decimal(18,8)", nullable: true),
                PricingDescription = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GeminiInvocationLogs", x => x.Id);
                table.ForeignKey("FK_GeminiInvocationLogs_Workspaces", x => x.WorkspaceId,
                    principalTable: "Workspaces", principalColumn: "WorkspaceId",
                    onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("CK_GeminiInvocationLogs_Attempt", "AttemptNumber > 0 AND AttemptNumber <= 100");
                table.CheckConstraint("CK_GeminiInvocationLogs_Duration", "DurationMs >= 0");
                table.CheckConstraint("CK_GeminiInvocationLogs_HttpStatus", "HttpStatus IS NULL OR HttpStatus BETWEEN 100 AND 599");
                table.CheckConstraint("CK_GeminiInvocationLogs_Source", "Source IN (N'Screening',N'PromptTest',N'Preview',N'Unknown')");
                table.CheckConstraint("CK_GeminiInvocationLogs_Outcome", "Outcome IN (N'HttpOk',N'HttpError',N'NetworkError',N'Timeout',N'Cancelled')");
                table.CheckConstraint("CK_GeminiInvocationLogs_Nonnegative", "(PromptTokens IS NULL OR PromptTokens >= 0) AND (CandidateTokens IS NULL OR CandidateTokens >= 0) AND (ThoughtsTokens IS NULL OR ThoughtsTokens >= 0) AND (CachedTokens IS NULL OR CachedTokens >= 0) AND (TotalTokens IS NULL OR TotalTokens >= 0) AND (EstimatedCostUsd IS NULL OR EstimatedCostUsd >= 0)");
            });
        migrationBuilder.CreateIndex("IX_GeminiInvocationLogs_OccurredAtUtc", "GeminiInvocationLogs", "OccurredAtUtc");
        migrationBuilder.CreateIndex("IX_GeminiInvocationLogs_Workspace_Time", "GeminiInvocationLogs", new[] { "WorkspaceId", "OccurredAtUtc" });
        migrationBuilder.CreateIndex("IX_GeminiInvocationLogs_Model_Time", "GeminiInvocationLogs", new[] { "ModelName", "OccurredAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "GeminiInvocationLogs");
}
