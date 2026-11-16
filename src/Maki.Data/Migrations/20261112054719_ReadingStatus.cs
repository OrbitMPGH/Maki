using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "UserSeriesStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "StatusAuto",
                table: "UserSeriesStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAt",
                table: "UserSeriesStates",
                type: "TEXT",
                nullable: true);

            // Every series somebody has already read starts as Reading for them; the rest stay Plan to read.
            // A high-water mark next to watched ticks or a pending anime marker came from Anime resume,
            // which is not reading.
            migrationBuilder.Sql("""
                INSERT INTO "UserSeriesStates" ("UserId", "SeriesId", "Status", "StatusAuto", "UpdatedAt",
                    "NotificationMode", "PendingRatingCheckpoint", "RatingCheckpointsSeen", "RatingPromptDismissals")
                SELECT DISTINCT r."UserId", r."SeriesId", 1, 0, strftime('%Y-%m-%d %H:%M:%S', 'now'), 0, 0, 0, 0
                FROM (
                    SELECT "UserId", "SeriesId" FROM "ChapterProgress"
                    WHERE "Watched" = 0 AND ("Completed" = 1 OR ("PageIndex" > 0 AND "UnreadAt" IS NULL))
                    UNION
                    SELECT rs."UserId", rs."SeriesId" FROM "ReadingStates" rs
                    WHERE rs."SeriesId" IS NOT NULL AND (rs."MaxChapter" > 0 OR rs."MaxVolume" > 0)
                    AND NOT EXISTS (SELECT 1 FROM "ChapterProgress" w
                        WHERE w."UserId" = rs."UserId" AND w."SeriesId" = rs."SeriesId" AND w."Watched" = 1)
                    AND NOT EXISTS (SELECT 1 FROM "UserSeriesStates" a
                        WHERE a."UserId" = rs."UserId" AND a."SeriesId" = rs."SeriesId" AND a."AnimeWatchPendingTo" IS NOT NULL)
                ) AS r
                JOIN "Series" s ON s."Id" = r."SeriesId"
                JOIN "AspNetUsers" u ON u."Id" = r."UserId"
                WHERE 1
                ON CONFLICT ("UserId", "SeriesId") DO UPDATE SET "Status" = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "StatusAuto",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "UserSeriesStates");
        }
    }
}
