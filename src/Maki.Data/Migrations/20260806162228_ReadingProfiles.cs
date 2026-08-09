using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadingProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReadingProfileId",
                table: "UserSeriesStates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReadingProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    PrefsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SeriesTypes = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadingProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReadingProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSeriesStates_ReadingProfileId",
                table: "UserSeriesStates",
                column: "ReadingProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingProfiles_UserId_Name",
                table: "ReadingProfiles",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_UserSeriesStates_ReadingProfiles_ReadingProfileId",
                table: "UserSeriesStates",
                column: "ReadingProfileId",
                principalTable: "ReadingProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Schema only. Giving existing accounts the built-in profiles is the next migration's
            // job: adding the foreign key above rebuilds UserSeriesStates, EF defers that rebuild to
            // the end of the migration, and any raw SQL sharing this one therefore runs against a
            // table state EF warns it cannot vouch for.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserSeriesStates_ReadingProfiles_ReadingProfileId",
                table: "UserSeriesStates");

            migrationBuilder.DropTable(
                name: "ReadingProfiles");

            migrationBuilder.DropIndex(
                name: "IX_UserSeriesStates_ReadingProfileId",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "ReadingProfileId",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Series");
        }
    }
}
