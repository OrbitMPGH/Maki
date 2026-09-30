using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// Queues the one-off "more appearance options" notice for everyone who already has an account,
    /// the same way <see cref="LanguageAnnouncement"/> did: the row is what makes the notice appear,
    /// so an account created after this release never gets one.
    /// </summary>
    public partial class AppearanceAnnouncement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                "INSERT OR IGNORE INTO UserSettings (UserId, \"Key\", Value) " +
                "SELECT Id, 'ui.appearanceannouncement', 'pending' FROM AspNetUsers WHERE PendingSetup = 0");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DELETE FROM UserSettings WHERE \"Key\" = 'ui.appearanceannouncement'");
    }
}
