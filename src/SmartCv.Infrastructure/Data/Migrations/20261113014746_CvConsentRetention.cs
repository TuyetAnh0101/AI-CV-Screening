using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CvConsentRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnonymizedAt",
                table: "Cvs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentConfirmedAt",
                table: "Cvs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetainUntil",
                table: "Cvs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceChannel",
                table: "Cvs",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cvs_RetainUntil",
                table: "Cvs",
                column: "RetainUntil",
                filter: "[AnonymizedAt] IS NULL AND [RetainUntil] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cvs_SourceChannel",
                table: "Cvs",
                sql: "SourceChannel IS NULL OR (SourceChannel COLLATE Latin1_General_100_BIN2 IN (N'Email', N'Website', N'JobBoard', N'Referral', N'Other') AND DATALENGTH(SourceChannel) = DATALENGTH(RTRIM(SourceChannel)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cvs_RetainUntil",
                table: "Cvs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cvs_SourceChannel",
                table: "Cvs");

            migrationBuilder.DropColumn(
                name: "AnonymizedAt",
                table: "Cvs");

            migrationBuilder.DropColumn(
                name: "ConsentConfirmedAt",
                table: "Cvs");

            migrationBuilder.DropColumn(
                name: "RetainUntil",
                table: "Cvs");

            migrationBuilder.DropColumn(
                name: "SourceChannel",
                table: "Cvs");
        }
    }
}
