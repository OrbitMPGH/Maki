using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mangarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSeriesMonitoredFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Monitored",
                table: "Series");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Monitored",
                table: "Series",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
