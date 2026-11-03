using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class HealthOperationRelink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RelinkFileId",
                table: "HealthOperations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RelinkFileId",
                table: "HealthOperations");
        }
    }
}
