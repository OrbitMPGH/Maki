using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpgradeProfileMaxTierScoreDrop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxTierScoreDrop",
                table: "UpgradeProfiles",
                type: "INTEGER",
                nullable: true);

            // MangaDex moved from Aggregator (1) to Scanlator (2); files already on disk keep the old stamp otherwise.
            migrationBuilder.Sql("UPDATE ChapterFiles SET Tier = 2 WHERE Tier = 1 AND lower(SourceName) = 'mangadex';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxTierScoreDrop",
                table: "UpgradeProfiles");
        }
    }
}
