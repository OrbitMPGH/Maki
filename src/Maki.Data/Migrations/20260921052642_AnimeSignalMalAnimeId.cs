using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnimeSignalMalAnimeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MalAnimeId",
                table: "AnimeSignals",
                type: "INTEGER",
                nullable: true);

            // A MyAnimeList row's own id already is the MyAnimeList id, so those backfill here
            // rather than waiting for a sync. AniList rows cannot: their idMal comes off the list
            // query, so they stay null until the next pass refreshes them, and until then they
            // simply fail to dedupe against their MyAnimeList twin - which is what happens today.
            migrationBuilder.Sql("UPDATE AnimeSignals SET MalAnimeId = AnimeId WHERE Service = 'mal'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MalAnimeId",
                table: "AnimeSignals");
        }
    }
}
