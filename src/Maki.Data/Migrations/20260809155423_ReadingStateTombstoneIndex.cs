using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadingStateTombstoneIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ReadingStates_Tombstones",
                table: "ReadingStates",
                columns: new[] { "KavitaSeriesId", "SeriesId" },
                filter: "\"SeriesId\" IS NULL AND \"KavitaSeriesId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReadingStates_Tombstones",
                table: "ReadingStates");
        }
    }
}
