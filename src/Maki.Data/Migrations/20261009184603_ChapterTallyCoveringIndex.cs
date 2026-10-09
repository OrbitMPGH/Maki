using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChapterTallyCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chapters_SeriesId_ChapterFileId_Wanted",
                table: "Chapters");

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_SeriesId_ChapterFileId_Wanted_FileRemovedAt",
                table: "Chapters",
                columns: new[] { "SeriesId", "ChapterFileId", "Wanted", "FileRemovedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chapters_SeriesId_ChapterFileId_Wanted_FileRemovedAt",
                table: "Chapters");

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_SeriesId_ChapterFileId_Wanted",
                table: "Chapters",
                columns: new[] { "SeriesId", "ChapterFileId", "Wanted" });
        }
    }
}
