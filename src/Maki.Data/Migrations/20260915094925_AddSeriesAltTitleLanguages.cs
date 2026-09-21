using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// <c>Series.AltTitles</c> went from <c>["a","b"]</c> to
    /// <c>[{"Title":"a","Language":null}, …]</c> when alt titles started carrying the language they
    /// are written in. The column is TEXT either way, so there is no schema change here — only the
    /// stored JSON, rewritten in place.
    /// <para>
    /// Rewriting it matters even though <c>SeriesMetadataRefreshService</c> overwrites the whole
    /// list on the next refresh: a series that never gets refreshed would keep the old shape
    /// forever, and the reader has to cope with it on every read. <c>LocalizedTitleListConverter</c>
    /// still parses the old shape as a safety net for databases restored from an older backup, but
    /// after this nothing in a live database is in it.
    /// </para>
    /// </summary>
    public partial class AddSeriesAltTitleLanguages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // json_type(…, '$[0]') is 'text' only for the old bare-string shape; an already-migrated
            // row answers 'object' and an empty array answers NULL, so both are skipped.
            migrationBuilder.Sql("""
                UPDATE Series
                SET AltTitles = (
                    SELECT json_group_array(json_object('Title', je.value, 'Language', null))
                    FROM json_each(Series.AltTitles) je)
                WHERE AltTitles IS NOT NULL
                  AND json_valid(AltTitles)
                  AND json_type(AltTitles, '$[0]') = 'text';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Series
                SET AltTitles = (
                    SELECT json_group_array(json_extract(je.value, '$.Title'))
                    FROM json_each(Series.AltTitles) je)
                WHERE AltTitles IS NOT NULL
                  AND json_valid(AltTitles)
                  AND json_type(AltTitles, '$[0]') = 'object';
                """);
        }
    }
}
