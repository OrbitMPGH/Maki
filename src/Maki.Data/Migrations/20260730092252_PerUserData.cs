using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class PerUserData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SeriesScrobbleStates_SeriesId_Service",
                table: "SeriesScrobbleStates");

            migrationBuilder.DropIndex(
                name: "IX_ScrobbleUnmatched_KavitaSeriesId_Service",
                table: "ScrobbleUnmatched");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ScrobbleTokens",
                table: "ScrobbleTokens");

            migrationBuilder.DropIndex(
                name: "IX_ScrobbleSyncStates_KavitaSeriesId_Service",
                table: "ScrobbleSyncStates");

            migrationBuilder.DropIndex(
                name: "IX_ScrobbleMappings_KavitaSeriesId_Service",
                table: "ScrobbleMappings");

            migrationBuilder.DropIndex(
                name: "IX_SavedFilters_SortOrder",
                table: "SavedFilters");

            migrationBuilder.DropIndex(
                name: "IX_ReadingStates_KavitaSeriesId",
                table: "ReadingStates");

            migrationBuilder.DropIndex(
                name: "IX_ReadingStates_NativeSeries",
                table: "ReadingStates");

            migrationBuilder.DropIndex(
                name: "IX_ReaderBookmarks_ChapterId_PageIndex",
                table: "ReaderBookmarks");

            migrationBuilder.DropIndex(
                name: "IX_ChapterProgress_ChapterId",
                table: "ChapterProgress");

            migrationBuilder.DropIndex(
                name: "IX_ChapterProgress_UpdatedAt",
                table: "ChapterProgress");

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "StatsEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "SeriesScrobbleStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "ScrobbleUnmatched",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "ScrobbleTokens",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "ScrobbleSyncStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "ScrobbleMappings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "ScrobbleLog",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "SavedFilters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "ReadingStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "ReaderBookmarks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "ChapterProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Everything that existed before there was more than one account belongs to the account
            // that was already using it: user 1, the row the MultiUserIdentity migration seeded and
            // the setup wizard claims. The columns above keep their DEFAULT 0 rather than defaulting
            // to 1 — a row that somehow arrives without an owner should be invisible to everybody
            // (fail-closed) instead of silently becoming user 1's.
            foreach (var table in new[]
                     {
                         "ChapterProgress", "ReaderBookmarks", "ReadingStates", "SavedFilters",
                         "ScrobbleLog", "ScrobbleMappings", "ScrobbleSyncStates", "ScrobbleTokens",
                         "ScrobbleUnmatched", "SeriesScrobbleStates",
                     })
            {
                migrationBuilder.Sql($"UPDATE \"{table}\" SET \"UserId\" = 1;");
            }

            // StatsEvents is the one table where null is meaningful, so only the *reading* half is
            // adopted. ChaptersRead (3), VolumesRead (4) and SeriesFinished (5) are somebody's
            // reading; SeriesAdded (0), SeriesRemoved (1) and ChapterDownloaded (2) describe the
            // library and stay null so Rewind keeps showing them to everyone — StatsBackfillService
            // seeded most of them from file timestamps, where no reader was ever recorded.
            migrationBuilder.Sql(
                "UPDATE \"StatsEvents\" SET \"UserId\" = 1 WHERE \"Type\" IN (3, 4, 5);");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ScrobbleTokens",
                table: "ScrobbleTokens",
                columns: new[] { "UserId", "Service" });

            migrationBuilder.CreateTable(
                name: "UserSeriesStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    ReaderPrefsJson = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSeriesStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSeriesStates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserSeriesStates_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSettings",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSettings", x => new { x.UserId, x.Key });
                    table.ForeignKey(
                        name: "FK_UserSettings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Rating and the per-series reader override were columns on the shared Series row, which
            // is exactly the bug: one person's score was what everybody saw, and what got pushed to
            // everybody's tracker profiles. Copy them to user 1's state rows before dropping them —
            // the scaffolded migration dropped first, which would have discarded every rating in the
            // library. Only rows that carry something are created, matching "no row means unrated,
            // reader defaults".
            migrationBuilder.Sql(
                """
                INSERT INTO "UserSeriesStates" ("UserId", "SeriesId", "Rating", "ReaderPrefsJson", "UpdatedAt")
                SELECT 1, "Id", "Rating", "ReaderPrefsJson", datetime('now')
                FROM "Series"
                WHERE "Rating" IS NOT NULL OR "ReaderPrefsJson" IS NOT NULL;
                """);

            // Per-user settings move out of the instance keyspace into user 1's rows. Copied, then
            // deleted from AppConfig: leaving them behind would mean two sources of truth for
            // "which page do I land on", and the losing one would win on the next release that read
            // the wrong store.
            migrationBuilder.Sql(
                """
                INSERT OR REPLACE INTO "UserSettings" ("UserId", "Key", "Value")
                SELECT 1, "Key", "Value" FROM "AppConfig"
                WHERE "Key" IN (
                        'reader.prefs', 'reader.pushtokavita', 'ui.startpage', 'ui.homesections',
                        'opds.enabled', 'opds.trackprogress', 'scrobble.plantoread',
                        'scrobble.mangabakatoken', 'scrobble.kitsuemail', 'scrobble.kitsupassword')
                   OR ("Key" LIKE 'scrobble.%.reading' OR "Key" LIKE 'scrobble.%.ratings');
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM "AppConfig"
                WHERE "Key" IN (
                        'reader.prefs', 'reader.pushtokavita', 'ui.startpage', 'ui.homesections',
                        'opds.enabled', 'opds.trackprogress', 'scrobble.plantoread',
                        'scrobble.mangabakatoken', 'scrobble.kitsuemail', 'scrobble.kitsupassword')
                   OR ("Key" LIKE 'scrobble.%.reading' OR "Key" LIKE 'scrobble.%.ratings');
                """);

            // The content-rating ceiling became a column on the user in the previous migration, which
            // already copied the value across; drop the stale instance key so nothing reads it again.
            migrationBuilder.Sql("DELETE FROM \"AppConfig\" WHERE \"Key\" = 'discover.maxcontentrating';");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "ReaderPrefsJson",
                table: "Series");

            migrationBuilder.CreateIndex(
                name: "IX_StatsEvents_UserId_Timestamp",
                table: "StatsEvents",
                columns: new[] { "UserId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesScrobbleStates_SeriesId",
                table: "SeriesScrobbleStates",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesScrobbleStates_UserId_SeriesId_Service",
                table: "SeriesScrobbleStates",
                columns: new[] { "UserId", "SeriesId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleUnmatched_UserId_KavitaSeriesId_Service",
                table: "ScrobbleUnmatched",
                columns: new[] { "UserId", "KavitaSeriesId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleSyncStates_UserId_KavitaSeriesId_Service",
                table: "ScrobbleSyncStates",
                columns: new[] { "UserId", "KavitaSeriesId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleMappings_UserId_KavitaSeriesId_Service",
                table: "ScrobbleMappings",
                columns: new[] { "UserId", "KavitaSeriesId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleLog_UserId_Timestamp",
                table: "ScrobbleLog",
                columns: new[] { "UserId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedFilters_UserId_SortOrder",
                table: "SavedFilters",
                columns: new[] { "UserId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ReadingStates_NativeSeries",
                table: "ReadingStates",
                columns: new[] { "UserId", "SeriesId" },
                unique: true,
                filter: "\"SeriesId\" IS NOT NULL AND \"KavitaSeriesId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingStates_UserId_KavitaSeriesId",
                table: "ReadingStates",
                columns: new[] { "UserId", "KavitaSeriesId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadingStates_UserSeries",
                table: "ReadingStates",
                columns: new[] { "UserId", "SeriesId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReaderBookmarks_ChapterId",
                table: "ReaderBookmarks",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_ReaderBookmarks_UserId_ChapterId_PageIndex",
                table: "ReaderBookmarks",
                columns: new[] { "UserId", "ChapterId", "PageIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReaderBookmarks_UserId_SeriesId",
                table: "ReaderBookmarks",
                columns: new[] { "UserId", "SeriesId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterProgress_ChapterId",
                table: "ChapterProgress",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterProgress_UserId_ChapterId",
                table: "ChapterProgress",
                columns: new[] { "UserId", "ChapterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChapterProgress_UserId_SeriesId",
                table: "ChapterProgress",
                columns: new[] { "UserId", "SeriesId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterProgress_UserId_UpdatedAt",
                table: "ChapterProgress",
                columns: new[] { "UserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSeriesStates_SeriesId",
                table: "UserSeriesStates",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSeriesStates_UserId_SeriesId",
                table: "UserSeriesStates",
                columns: new[] { "UserId", "SeriesId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ChapterProgress_AspNetUsers_UserId",
                table: "ChapterProgress",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReaderBookmarks_AspNetUsers_UserId",
                table: "ReaderBookmarks",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReadingStates_AspNetUsers_UserId",
                table: "ReadingStates",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SavedFilters_AspNetUsers_UserId",
                table: "SavedFilters",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ScrobbleLog_AspNetUsers_UserId",
                table: "ScrobbleLog",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ScrobbleMappings_AspNetUsers_UserId",
                table: "ScrobbleMappings",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ScrobbleSyncStates_AspNetUsers_UserId",
                table: "ScrobbleSyncStates",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ScrobbleTokens_AspNetUsers_UserId",
                table: "ScrobbleTokens",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ScrobbleUnmatched_AspNetUsers_UserId",
                table: "ScrobbleUnmatched",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SeriesScrobbleStates_AspNetUsers_UserId",
                table: "SeriesScrobbleStates",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StatsEvents_AspNetUsers_UserId",
                table: "StatsEvents",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChapterProgress_AspNetUsers_UserId",
                table: "ChapterProgress");

            migrationBuilder.DropForeignKey(
                name: "FK_ReaderBookmarks_AspNetUsers_UserId",
                table: "ReaderBookmarks");

            migrationBuilder.DropForeignKey(
                name: "FK_ReadingStates_AspNetUsers_UserId",
                table: "ReadingStates");

            migrationBuilder.DropForeignKey(
                name: "FK_SavedFilters_AspNetUsers_UserId",
                table: "SavedFilters");

            migrationBuilder.DropForeignKey(
                name: "FK_ScrobbleLog_AspNetUsers_UserId",
                table: "ScrobbleLog");

            migrationBuilder.DropForeignKey(
                name: "FK_ScrobbleMappings_AspNetUsers_UserId",
                table: "ScrobbleMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_ScrobbleSyncStates_AspNetUsers_UserId",
                table: "ScrobbleSyncStates");

            migrationBuilder.DropForeignKey(
                name: "FK_ScrobbleTokens_AspNetUsers_UserId",
                table: "ScrobbleTokens");

            migrationBuilder.DropForeignKey(
                name: "FK_ScrobbleUnmatched_AspNetUsers_UserId",
                table: "ScrobbleUnmatched");

            migrationBuilder.DropForeignKey(
                name: "FK_SeriesScrobbleStates_AspNetUsers_UserId",
                table: "SeriesScrobbleStates");

            migrationBuilder.DropForeignKey(
                name: "FK_StatsEvents_AspNetUsers_UserId",
                table: "StatsEvents");

            migrationBuilder.DropTable(
                name: "UserSeriesStates");

            migrationBuilder.DropTable(
                name: "UserSettings");

            migrationBuilder.DropIndex(
                name: "IX_StatsEvents_UserId_Timestamp",
                table: "StatsEvents");

            migrationBuilder.DropIndex(
                name: "IX_SeriesScrobbleStates_SeriesId",
                table: "SeriesScrobbleStates");

            migrationBuilder.DropIndex(
                name: "IX_SeriesScrobbleStates_UserId_SeriesId_Service",
                table: "SeriesScrobbleStates");

            migrationBuilder.DropIndex(
                name: "IX_ScrobbleUnmatched_UserId_KavitaSeriesId_Service",
                table: "ScrobbleUnmatched");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ScrobbleTokens",
                table: "ScrobbleTokens");

            migrationBuilder.DropIndex(
                name: "IX_ScrobbleSyncStates_UserId_KavitaSeriesId_Service",
                table: "ScrobbleSyncStates");

            migrationBuilder.DropIndex(
                name: "IX_ScrobbleMappings_UserId_KavitaSeriesId_Service",
                table: "ScrobbleMappings");

            migrationBuilder.DropIndex(
                name: "IX_ScrobbleLog_UserId_Timestamp",
                table: "ScrobbleLog");

            migrationBuilder.DropIndex(
                name: "IX_SavedFilters_UserId_SortOrder",
                table: "SavedFilters");

            migrationBuilder.DropIndex(
                name: "IX_ReadingStates_NativeSeries",
                table: "ReadingStates");

            migrationBuilder.DropIndex(
                name: "IX_ReadingStates_UserId_KavitaSeriesId",
                table: "ReadingStates");

            migrationBuilder.DropIndex(
                name: "IX_ReadingStates_UserSeries",
                table: "ReadingStates");

            migrationBuilder.DropIndex(
                name: "IX_ReaderBookmarks_ChapterId",
                table: "ReaderBookmarks");

            migrationBuilder.DropIndex(
                name: "IX_ReaderBookmarks_UserId_ChapterId_PageIndex",
                table: "ReaderBookmarks");

            migrationBuilder.DropIndex(
                name: "IX_ReaderBookmarks_UserId_SeriesId",
                table: "ReaderBookmarks");

            migrationBuilder.DropIndex(
                name: "IX_ChapterProgress_ChapterId",
                table: "ChapterProgress");

            migrationBuilder.DropIndex(
                name: "IX_ChapterProgress_UserId_ChapterId",
                table: "ChapterProgress");

            migrationBuilder.DropIndex(
                name: "IX_ChapterProgress_UserId_SeriesId",
                table: "ChapterProgress");

            migrationBuilder.DropIndex(
                name: "IX_ChapterProgress_UserId_UpdatedAt",
                table: "ChapterProgress");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "StatsEvents");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "SeriesScrobbleStates");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ScrobbleUnmatched");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ScrobbleTokens");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ScrobbleSyncStates");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ScrobbleMappings");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ScrobbleLog");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "SavedFilters");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ReadingStates");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ReaderBookmarks");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ChapterProgress");

            migrationBuilder.AddColumn<int>(
                name: "Rating",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReaderPrefsJson",
                table: "Series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ScrobbleTokens",
                table: "ScrobbleTokens",
                column: "Service");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesScrobbleStates_SeriesId_Service",
                table: "SeriesScrobbleStates",
                columns: new[] { "SeriesId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleUnmatched_KavitaSeriesId_Service",
                table: "ScrobbleUnmatched",
                columns: new[] { "KavitaSeriesId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleSyncStates_KavitaSeriesId_Service",
                table: "ScrobbleSyncStates",
                columns: new[] { "KavitaSeriesId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleMappings_KavitaSeriesId_Service",
                table: "ScrobbleMappings",
                columns: new[] { "KavitaSeriesId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedFilters_SortOrder",
                table: "SavedFilters",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingStates_KavitaSeriesId",
                table: "ReadingStates",
                column: "KavitaSeriesId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadingStates_NativeSeries",
                table: "ReadingStates",
                column: "SeriesId",
                unique: true,
                filter: "\"SeriesId\" IS NOT NULL AND \"KavitaSeriesId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReaderBookmarks_ChapterId_PageIndex",
                table: "ReaderBookmarks",
                columns: new[] { "ChapterId", "PageIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChapterProgress_ChapterId",
                table: "ChapterProgress",
                column: "ChapterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChapterProgress_UpdatedAt",
                table: "ChapterProgress",
                column: "UpdatedAt");
        }
    }
}
