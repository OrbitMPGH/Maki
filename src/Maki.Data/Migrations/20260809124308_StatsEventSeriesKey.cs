using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class StatsEventSeriesKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SeriesKey",
                table: "StatsEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatsEvents_SeriesKey",
                table: "StatsEvents",
                column: "SeriesKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StatsEvents_SeriesKey",
                table: "StatsEvents");

            migrationBuilder.DropColumn(
                name: "SeriesKey",
                table: "StatsEvents");
        }
    }
}
