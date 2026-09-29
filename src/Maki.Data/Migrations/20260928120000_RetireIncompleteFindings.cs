using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// Closes the incomplete findings. Partial analysis is now a hint on the file's analysis, not a
    /// finding, and a scan no longer creates or re-evaluates these rows. Resolved rather than deleted,
    /// so the history of what was once reported survives.
    /// </summary>
    public partial class RetireIncompleteFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                "UPDATE HealthFindings SET State = 'resolved' " +
                "WHERE Kind = 'incomplete' AND State <> 'resolved'");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
