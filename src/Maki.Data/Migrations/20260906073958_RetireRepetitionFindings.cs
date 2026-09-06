using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// Closes the findings whose checks no longer exist.
    /// </summary>
    /// <remarks>
    /// Nothing produces pageRepetition or blankRepetition any more, and nothing re-evaluates them
    /// either: a file is only re-analysed when its bytes or its analyzer version change, so open
    /// rows would sit in the workspace forever with no way to clear them. Resolved rather than
    /// deleted, so the history of what was once reported survives.
    /// </remarks>
    public partial class RetireRepetitionFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                "UPDATE HealthFindings SET State = 'resolved' " +
                "WHERE Kind IN ('pageRepetition', 'blankRepetition') AND State <> 'resolved'");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Which of these were open is not recorded, and the checks that raised them are gone.
        }
    }
}
