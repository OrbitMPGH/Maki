using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mangarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeriesNumberingClash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NumberingClash",
                table: "Series",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NumberingClash",
                table: "Series");
        }
    }
}
