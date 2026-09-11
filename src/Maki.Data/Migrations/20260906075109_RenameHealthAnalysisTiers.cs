using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameHealthAnalysisTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Deep",
                table: "HealthScans",
                newName: "Verify");

            migrationBuilder.RenameColumn(
                name: "DeepVersion",
                table: "HealthFiles",
                newName: "VerifiedVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Verify",
                table: "HealthScans",
                newName: "Deep");

            migrationBuilder.RenameColumn(
                name: "VerifiedVersion",
                table: "HealthFiles",
                newName: "DeepVersion");
        }
    }
}
