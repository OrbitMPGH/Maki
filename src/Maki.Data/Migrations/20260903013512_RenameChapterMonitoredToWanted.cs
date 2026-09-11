using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// Renames <c>Chapters.Monitored</c> to <c>Chapters.Wanted</c> and repairs the rows the download
    /// pipeline used to own.
    /// <para>
    /// The flag now means one thing — the user wants this chapter — and nothing but the user and the
    /// initial stamp writes it. On a Smart series that was not true before: <c>SmartDownloadJob</c>
    /// rewrote every flag on each top-up so only its ten-chapter window stayed set. Left alone those
    /// rows would read as deliberate exclusions and permanently shrink the series' chapter total, so
    /// they are switched back on. Every other mode keeps its flags exactly as they are, because there
    /// they really are the user's choice (MainOnly's skipped specials, manual un-ticks).
    /// </para>
    /// <para>
    /// Specials are held back where <c>monitoring.unmonitorspecials</c> is on, so a Smart series
    /// comes out of this reading the same as an equivalent MainOnly one. The setting lives in
    /// AppConfig in this same database, so no startup fixup is needed to see it.
    /// </para>
    /// </summary>
    public partial class RenameChapterMonitoredToWanted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Monitored",
                table: "Chapters",
                newName: "Wanted");

            // MonitorNewItems 3 = NewChapterMonitorMode.Smart. A series that used to be Smart and has
            // since been switched to another mode is indistinguishable from one that never was, so it
            // keeps whatever it has — that user picked a mode afterwards.
            migrationBuilder.Sql(@"
                UPDATE Chapters SET Wanted = 1
                WHERE SeriesId IN (SELECT Id FROM Series WHERE MonitorNewItems = 3)
                  AND (COALESCE((SELECT Value FROM AppConfig WHERE Key = 'monitoring.unmonitorspecials'), 'false') <> 'true'
                       OR Number IS NULL
                       OR Number = CAST(Number AS INTEGER));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The flags this overwrote were machine state with no user meaning, so there is nothing
            // to restore; only the rename comes back.
            migrationBuilder.RenameColumn(
                name: "Wanted",
                table: "Chapters",
                newName: "Monitored");
        }
    }
}
