using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    AuditEventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ActorUserId = table.Column<int>(type: "int", nullable: true),
                    ActorNameSnapshot = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ActorRoleSnapshot = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ActionCode = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    Category = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ResourceType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ResourceId = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: true),
                    Outcome = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RequestId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SafeSummary = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.AuditEventId);
                    table.CheckConstraint("CK_AuditEvents_Actor", "([ActorUserId] IS NULL AND [ActorNameSnapshot] IS NULL AND [ActorRoleSnapshot] IS NULL) OR ([ActorUserId] IS NOT NULL AND [ActorUserId] > 0 AND [ActorNameSnapshot] IS NOT NULL AND LEN([ActorNameSnapshot]) > 0 AND [ActorRoleSnapshot] IS NOT NULL AND [ActorRoleSnapshot] COLLATE Latin1_General_100_BIN2 IN (N'Admin',N'HR'))");
                    table.CheckConstraint("CK_AuditEvents_Outcome", "[Outcome] COLLATE Latin1_General_100_BIN2 IN ('Succeeded','Denied','Failed','Partial','NoChange','AccessGranted')");
                    table.ForeignKey(
                        name: "FK_AuditEvents_Users",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action_Time",
                table: "AuditEvents",
                columns: new[] { "ActionCode", "OccurredAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Actor_Time",
                table: "AuditEvents",
                columns: new[] { "ActorUserId", "OccurredAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Category_Outcome_Time",
                table: "AuditEvents",
                columns: new[] { "Category", "Outcome", "OccurredAtUtc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Request",
                table: "AuditEvents",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Resource_Time",
                table: "AuditEvents",
                columns: new[] { "ResourceType", "ResourceId", "OccurredAtUtc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Time",
                table: "AuditEvents",
                columns: new[] { "OccurredAtUtc", "AuditEventId" },
                descending: new bool[0]);

            // Only the new audit table is affected. A35's ApplicationEvents trigger remains untouched.
            // EXEC keeps CREATE TRIGGER first in its own batch, so the idempotent script
            // (dotnet ef migrations script --idempotent) can wrap it in IF NOT EXISTS like A35 does.
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER [dbo].[TR_AuditEvents_NoChange]
                ON [dbo].[AuditEvents] AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51043, N''Audit events are append-only.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DROP TABLE also drops the table's DML trigger TR_AuditEvents_NoChange.
            migrationBuilder.DropTable(
                name: "AuditEvents");
        }
    }
}
