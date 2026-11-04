using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// Folds a score changed again at the same chapter into one history entry, the rule
    /// <c>RatingSnapshotService.RecordAsync</c> now follows: each entry followed by one of the same
    /// kind at the same chapter is dropped, so the latest score of the run is the one kept.
    /// </summary>
    public partial class MergeRatingCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "RatingSnapshots" WHERE "Id" IN (
                    SELECT "Id" FROM (
                        SELECT "Id", "Final", "AtChapter",
                            LEAD("Id") OVER w AS "NextId",
                            LEAD("Final") OVER w AS "NextFinal",
                            LEAD("AtChapter") OVER w AS "NextAt"
                        FROM "RatingSnapshots"
                        WINDOW w AS (PARTITION BY "UserId", "SeriesId" ORDER BY "CreatedAt", "Id")
                    )
                    WHERE "NextId" IS NOT NULL AND "NextFinal" = "Final" AND "NextAt" IS "AtChapter"
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
