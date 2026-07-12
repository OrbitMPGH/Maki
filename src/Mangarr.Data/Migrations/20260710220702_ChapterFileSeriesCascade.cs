using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mangarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChapterFileSeriesCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows orphaned by series deletions from before this FK existed would
            // violate the constraint the moment the table is rebuilt.
            migrationBuilder.Sql("DELETE FROM ChapterFiles WHERE SeriesId NOT IN (SELECT Id FROM Series);");

            migrationBuilder.AddForeignKey(
                name: "FK_ChapterFiles_Series_SeriesId",
                table: "ChapterFiles",
                column: "SeriesId",
                principalTable: "Series",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChapterFiles_Series_SeriesId",
                table: "ChapterFiles");
        }
    }
}
