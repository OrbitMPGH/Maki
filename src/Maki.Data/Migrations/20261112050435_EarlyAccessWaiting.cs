using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// Moves chapters parked for early access from Failed (7) to the new Waiting status (12). Only the
    /// newest such row per chapter moves, and only while the chapter has no other active row and still
    /// lacks a file, since Waiting counts as active for the one-active-row-per-chapter index.
    /// </summary>
    public partial class EarlyAccessWaiting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "DownloadQueue" SET "Status" = 12
                WHERE "Status" = 7
                  AND "ErrorKey" = 'error.download.earlyAccess'
                  AND "ChapterId" IS NOT NULL
                  AND "Id" = (
                      SELECT MAX(o."Id") FROM "DownloadQueue" o
                      WHERE o."ChapterId" = "DownloadQueue"."ChapterId"
                        AND o."Status" = 7 AND o."ErrorKey" = 'error.download.earlyAccess')
                  AND NOT EXISTS (
                      SELECT 1 FROM "DownloadQueue" a
                      WHERE a."ChapterId" = "DownloadQueue"."ChapterId" AND a."Status" NOT IN (6, 7, 8))
                  AND EXISTS (
                      SELECT 1 FROM "Chapters" c
                      WHERE c."Id" = "DownloadQueue"."ChapterId" AND c."ChapterFileId" IS NULL AND c."FileRemovedAt" IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "DownloadQueue" SET "Status" = 7 WHERE "Status" = 12;""");
        }
    }
}
