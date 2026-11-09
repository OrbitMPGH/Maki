using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// Folds a score changed again at the same chapter within ten minutes into one history entry,
    /// the rule <c>RatingSnapshotService.RecordAsync</c> now follows: each entry followed by one of the
    /// same kind at the same chapter less than ten minutes later is dropped, so the latest score of the
    /// run is the one kept, carrying the earlier entry's origin.
    /// </summary>
    public partial class MergeRatingCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            const string pairs = """
                SELECT "Id", "Origin",
                    LEAD("Id") OVER w AS "NextId",
                    CASE WHEN LEAD("Final") OVER w = "Final"
                              AND LEAD("AtChapter") OVER w IS "AtChapter"
                              AND (julianday(LEAD("CreatedAt") OVER w) - julianday("CreatedAt")) * 1440.0 < 10
                         THEN 1 ELSE 0 END AS "Merge"
                FROM "RatingSnapshots"
                WINDOW w AS (PARTITION BY "UserId", "SeriesId" ORDER BY "CreatedAt", "Id")
                """;

            migrationBuilder.Sql($"""
                UPDATE "RatingSnapshots" SET "Origin" = (
                    SELECT p."Origin" FROM ({pairs}) p WHERE p."Merge" = 1 AND p."NextId" = "RatingSnapshots"."Id"
                )
                WHERE "Id" IN (SELECT "NextId" FROM ({pairs}) WHERE "Merge" = 1);
                """);

            migrationBuilder.Sql($"""
                DELETE FROM "RatingSnapshots" WHERE "Id" IN (SELECT "Id" FROM ({pairs}) WHERE "Merge" = 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
