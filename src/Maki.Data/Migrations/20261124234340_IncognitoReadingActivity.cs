using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class IncognitoReadingActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReadingSessions_UserId_EndedAt",
                table: "ReadingSessions");

            migrationBuilder.AddColumn<bool>(
                name: "HiddenAfterRemoval",
                table: "StatsEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HiddenAfterRemoval",
                table: "ReadingSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // SQLite can add a nullable reference directly. EF's AddForeignKey rebuild would run
            // after the trigger SQL and fail while ReadingSessions was temporarily missing.
            migrationBuilder.Sql("""
                ALTER TABLE ReadingSessions ADD COLUMN SeriesId INTEGER NULL
                    CONSTRAINT FK_ReadingSessions_Series_SeriesId REFERENCES Series (Id) ON DELETE SET NULL;
                """);

            migrationBuilder.AddColumn<string>(
                name: "SeriesKey",
                table: "ReadingSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadingSessions_SeriesId",
                table: "ReadingSessions",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingSessions_SeriesKey",
                table: "ReadingSessions",
                column: "SeriesKey");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingSessions_UserId_SeriesId_EndedAt",
                table: "ReadingSessions",
                columns: new[] { "UserId", "SeriesId", "EndedAt" });

            migrationBuilder.Sql(ActivityPrivacyTrigger.Create);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ActivityPrivacyTrigger.Drop);

            migrationBuilder.DropForeignKey(
                name: "FK_ReadingSessions_Series_SeriesId",
                table: "ReadingSessions");

            migrationBuilder.DropIndex(
                name: "IX_ReadingSessions_SeriesId",
                table: "ReadingSessions");

            migrationBuilder.DropIndex(
                name: "IX_ReadingSessions_SeriesKey",
                table: "ReadingSessions");

            migrationBuilder.DropIndex(
                name: "IX_ReadingSessions_UserId_SeriesId_EndedAt",
                table: "ReadingSessions");

            migrationBuilder.DropColumn(
                name: "HiddenAfterRemoval",
                table: "StatsEvents");

            migrationBuilder.DropColumn(
                name: "HiddenAfterRemoval",
                table: "ReadingSessions");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "ReadingSessions");

            migrationBuilder.DropColumn(
                name: "SeriesKey",
                table: "ReadingSessions");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingSessions_UserId_EndedAt",
                table: "ReadingSessions",
                columns: new[] { "UserId", "EndedAt" });
        }
    }
}
