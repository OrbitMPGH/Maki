using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class DownloadQueueActiveChapter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column fills itself from Status, but duplicate active rows from before the index
            // would make the index fail to build. Keep the oldest per chapter, cancel the rest.
            migrationBuilder.Sql("""
                UPDATE "DownloadQueue" SET "Status" = 8
                WHERE "ChapterId" IS NOT NULL
                  AND "Status" NOT IN (6, 7, 8)
                  AND EXISTS (
                      SELECT 1 FROM "DownloadQueue" AS older
                      WHERE older."ChapterId" = "DownloadQueue"."ChapterId"
                        AND older."Status" NOT IN (6, 7, 8)
                        AND older."Id" < "DownloadQueue"."Id");
                """);

            migrationBuilder.AddColumn<int>(
                name: "ActiveChapterId",
                table: "DownloadQueue",
                type: "INTEGER",
                nullable: true,
                computedColumnSql: "CASE WHEN \"Status\" IN (6, 7, 8) THEN NULL ELSE \"ChapterId\" END",
                stored: false);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_ActiveChapterId",
                table: "DownloadQueue",
                column: "ActiveChapterId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_ActiveChapterId",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "ActiveChapterId",
                table: "DownloadQueue");
        }
    }
}
