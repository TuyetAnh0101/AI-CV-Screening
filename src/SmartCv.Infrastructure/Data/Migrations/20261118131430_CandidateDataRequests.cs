using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CandidateDataRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CandidateDataRequests",
                columns: table => new
                {
                    RequestId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CandidateId = table.Column<int>(type: "int", nullable: false),
                    RequestType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    HandledBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ResultSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CvIds = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                        .Annotation("Relational:DefaultConstraintName", "DF_CandidateDataRequests_CreatedAt")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateDataRequests", x => x.RequestId);
                    table.CheckConstraint("CK_CandidateDataRequests_RequestType", "[RequestType] COLLATE Latin1_General_100_BIN2 IN (N'Export', N'Erase')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateDataRequests_Candidate_CreatedAt",
                table: "CandidateDataRequests",
                columns: new[] { "CandidateId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandidateDataRequests");
        }
    }
}
