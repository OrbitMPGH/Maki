using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeriesRequestPendingUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two identical pending requests are only noise, but the index below refuses the second
            // one outright, so any duplicates already sitting in the table have to go first (oldest
            // row wins — it is the one the requester and any admin already looking at the queue know
            // about).
            migrationBuilder.Sql("""
                DELETE FROM SeriesRequests
                WHERE Status = 0
                  AND Id NOT IN (
                    SELECT MIN(Id) FROM SeriesRequests
                    WHERE Status = 0
                    GROUP BY
                        UserId,
                        Kind,
                        COALESCE(MetadataProviderId, ''),
                        COALESCE(SeriesId, -1),
                        COALESCE(ChapterStart, -1),
                        COALESCE(ChapterEnd, -1)
                  );
                """);

            // Plain multi-column UNIQUE treats two NULLs as distinct, which is exactly the shape most
            // rows have (a NewSeries request has no SeriesId, a Chapters request has no
            // MetadataProviderId) — so every column is coalesced to a sentinel outside its normal
            // range first. Partial (WHERE Status = 0/Pending only): a resolved duplicate is history,
            // not noise, and re-requesting the same range after a rejection must still be possible.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IX_SeriesRequests_Pending_Identity
                ON SeriesRequests (
                    UserId,
                    Kind,
                    COALESCE(MetadataProviderId, ''),
                    COALESCE(SeriesId, -1),
                    COALESCE(ChapterStart, -1),
                    COALESCE(ChapterEnd, -1)
                )
                WHERE Status = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_SeriesRequests_Pending_Identity;");
        }
    }
}
