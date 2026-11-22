using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailOutbox",
                columns: table => new
                {
                    EmailOutboxId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    ToAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Template = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    ProtectedPayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Attempts = table.Column<byte>(type: "tinyint", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailOutbox", x => x.EmailOutboxId);
                    table.CheckConstraint("CK_EmailOutbox_Attempts", "[Attempts] BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_EmailOutbox_Claim", "([ClaimId] IS NULL AND [LeaseUntilUtc] IS NULL) OR ([ClaimId] IS NOT NULL AND [LeaseUntilUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_EmailOutbox_Payload", "([Status] = 'Pending' AND [ProtectedPayload] IS NOT NULL) OR ([Status] <> 'Pending' AND [ProtectedPayload] IS NULL)");
                    table.CheckConstraint("CK_EmailOutbox_Status", "[Status] IN ('Pending','Sent','Failed')");
                    table.ForeignKey(
                        name: "FK_EmailOutbox_Users",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_Claim",
                table: "EmailOutbox",
                columns: new[] { "ClaimId", "EmailOutboxId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_Status_NextAttemptAt",
                table: "EmailOutbox",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_User_Template_Created",
                table: "EmailOutbox",
                columns: new[] { "UserId", "Template", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailOutbox");
        }
    }
}
