using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnimeSignalRetryMalLookups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MalTracker.RelatedMangaAsync used to call MAL's own related_manga field, which always
            // comes back empty - every "mal" row a sync ever looked up got stamped with no manga id.
            // Clearing the stamp lets the next sync retry them, now through AniList's public GraphQL.
            migrationBuilder.Sql(
                "UPDATE AnimeSignals SET MatchAttemptedAtUtc = NULL " +
                "WHERE Service = 'mal' AND MalMangaId IS NULL AND AniListMangaId IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
