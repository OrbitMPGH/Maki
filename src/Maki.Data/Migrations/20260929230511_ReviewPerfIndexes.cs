using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewPerfIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChapterFiles_SeriesId",
                table: "ChapterFiles");

            migrationBuilder.CreateIndex(
                name: "IX_HealthFiles_SeriesId",
                table: "HealthFiles",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_HealthOperationId",
                table: "DownloadQueue",
                column: "HealthOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_SortOrder",
                table: "DownloadQueue",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_SortOrder_QueuedAt",
                table: "DownloadQueue",
                columns: new[] { "SortOrder", "QueuedAt" },
                filter: "\"Status\" NOT IN (6, 8)");

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_SeriesId_ChapterFileId_Wanted",
                table: "Chapters",
                columns: new[] { "SeriesId", "ChapterFileId", "Wanted" });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterFiles_SeriesId_SourceName",
                table: "ChapterFiles",
                columns: new[] { "SeriesId", "SourceName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HealthFiles_SeriesId",
                table: "HealthFiles");

            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_HealthOperationId",
                table: "DownloadQueue");

            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_SortOrder",
                table: "DownloadQueue");

            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_SortOrder_QueuedAt",
                table: "DownloadQueue");

            migrationBuilder.DropIndex(
                name: "IX_Chapters_SeriesId_ChapterFileId_Wanted",
                table: "Chapters");

            migrationBuilder.DropIndex(
                name: "IX_ChapterFiles_SeriesId_SourceName",
                table: "ChapterFiles");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterFiles_SeriesId",
                table: "ChapterFiles",
                column: "SeriesId");
        }
    }
}
