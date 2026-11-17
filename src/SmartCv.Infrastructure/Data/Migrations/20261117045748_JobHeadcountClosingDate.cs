using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobHeadcountClosingDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ClosingDate",
                table: "Jobs",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Headcount",
                table: "Jobs",
                type: "int",
                nullable: true);

            // Both new nullable columns already contain NULL for every existing row; no seed UPDATE is needed.
            migrationBuilder.AddCheckConstraint(
                name: "CK_Jobs_Headcount",
                table: "Jobs",
                sql: "Headcount IS NULL OR Headcount BETWEEN 1 AND 1000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Jobs_Headcount",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ClosingDate",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "Headcount",
                table: "Jobs");
        }
    }
}
