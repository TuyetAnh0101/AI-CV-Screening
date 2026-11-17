using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobTitleUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "UQ_Jobs_Title",
                table: "Jobs");

            migrationBuilder.CreateIndex(
                name: "UQ_Jobs_Title",
                table: "Jobs",
                column: "Title",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_Jobs_Title",
                table: "Jobs");

            migrationBuilder.AddUniqueConstraint(
                name: "UQ_Jobs_Title",
                table: "Jobs",
                column: "Title");
        }
    }
}
