using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// Gives every account that already exists the same three reading profiles
    /// <c>ReadingProfileSeeder</c> hands a new one. Its own migration rather than part of
    /// <c>ReadingProfiles</c>: that one adds a foreign key to <c>UserSeriesStates</c>, SQLite has to
    /// rebuild the table for it, and EF defers the rebuild to the end of the migration — so raw SQL
    /// sharing it runs against a state EF explicitly warns it cannot vouch for.
    /// <para>
    /// The values are literal here rather than read from <c>ReadingProfileDefaults</c> on purpose. A
    /// migration is a snapshot of one version: retuning the shipped defaults later must change what
    /// a <em>new</em> account gets, not rewrite an upgrade that has already run somewhere.
    /// </para>
    /// <para>
    /// Nothing else is backfilled. <c>Series.Type</c> lands null on every existing row and is filled
    /// by the daily metadata job or the Library's bulk "Metadata" action; a null type matches no
    /// profile, so until then the reader behaves exactly as it did before profiles existed.
    /// </para>
    /// </summary>
    public partial class ReadingProfileSeed : Migration
    {
        /// <summary>
        /// Fixed rather than <c>DateTime.UtcNow</c>: a migration must produce the same database
        /// whenever it is applied, and these two columns are only ever displayed.
        /// </summary>
        private const string SeededAt = "2026-08-06 16:31:53";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (name, prefs, types) in new[]
                     {
                         ("Manga",
                             """{"mode":"paged","direction":"rtl","fit":"height","pageGap":0,"preload":3,"tapZones":true,"showPageNumber":true,"splitWidePages":false,"autoNextChapter":true,"background":"#0a0a0b"}""",
                             "manga"),
                         ("Webtoon",
                             """{"mode":"vertical","direction":"ltr","fit":"original","pageGap":0,"preload":3,"tapZones":true,"showPageNumber":true,"splitWidePages":false,"autoNextChapter":true,"background":"#0a0a0b"}""",
                             "manhwa,manhua"),
                         ("Comic",
                             """{"mode":"paged","direction":"ltr","fit":"height","pageGap":0,"preload":3,"tapZones":true,"showPageNumber":true,"splitWidePages":false,"autoNextChapter":true,"background":"#0a0a0b"}""",
                             "oel"),
                     })
            {
                // NOT EXISTS rather than a plain insert: (UserId, Name) is uniquely indexed, and a
                // user who already holds a profile of that name must not turn the upgrade into a
                // startup crash.
                migrationBuilder.Sql($"""
                    INSERT INTO ReadingProfiles (UserId, Name, PrefsJson, SeriesTypes, CreatedAt, UpdatedAt)
                    SELECT u.Id, '{name}', '{prefs}', '{types}', '{SeededAt}', '{SeededAt}'
                    FROM AspNetUsers u
                    WHERE NOT EXISTS (
                        SELECT 1 FROM ReadingProfiles p WHERE p.UserId = u.Id AND p.Name = '{name}');
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing: by the time anyone rolls back these are ordinary profiles that may have been
            // renamed or retuned, so there is no honest way to tell a seeded one from a hand-written
            // one.
        }
    }
}
