using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// Queues the one-off "Maki speaks your language now" notice for everyone who already has an
    /// account.
    /// </summary>
    /// <remarks>
    /// The row is what makes the notice appear, so writing it here — once, at the upgrade — is what
    /// scopes it to existing users: an account created afterwards never gets one, and somebody whose
    /// first Maki already spoke their language is not told about a change they never lived through.
    /// The unclaimed setup placeholder is skipped for the same reason; it is not a person yet.
    /// <para>
    /// INSERT OR IGNORE because (UserId, Key) is the primary key and this must not fight a row that
    /// somehow already exists. Nothing else ever writes "pending".
    /// </para>
    /// </remarks>
    public partial class LanguageAnnouncement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                "INSERT OR IGNORE INTO UserSettings (UserId, \"Key\", Value) " +
                "SELECT Id, 'ui.languageannouncement', 'pending' FROM AspNetUsers WHERE PendingSetup = 0");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DELETE FROM UserSettings WHERE \"Key\" = 'ui.languageannouncement'");
    }
}
