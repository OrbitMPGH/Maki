using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class HealthWorkspaceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_HealthFindings_State",
                table: "HealthFindings",
                column: "State",
                filter: "\"State\" = 'open'");

            migrationBuilder.CreateIndex(
                name: "IX_HealthFiles_RelativePath",
                table: "HealthFiles",
                column: "RelativePath",
                filter: "NOT \"Removed\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HealthFindings_State",
                table: "HealthFindings");

            migrationBuilder.DropIndex(
                name: "IX_HealthFiles_RelativePath",
                table: "HealthFiles");
        }
    }
}
