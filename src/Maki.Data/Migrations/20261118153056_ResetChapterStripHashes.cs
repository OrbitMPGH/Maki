using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResetChapterStripHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Strips made before two-page spreads were split in two; the page check fetches them again.
            migrationBuilder.Sql("UPDATE \"ChapterSourceLinks\" SET \"StripHashes\" = NULL WHERE \"StripHashes\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
