using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChapterFileNamedByMaki : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NamedByMaki",
                table: "ChapterFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Scraper downloads were always named by Maki. A torrent row may have been renamed since,
            // and nothing records whether it was, so it keeps reading its group off the release title
            // only, as it did before this column.
            migrationBuilder.Sql(
                "UPDATE ChapterFiles SET NamedByMaki = 1 WHERE SourceChapterId IS NOT NULL OR ReleaseName IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NamedByMaki",
                table: "ChapterFiles");
        }
    }
}
