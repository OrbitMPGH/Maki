using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpgradeProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UpgradeProfileId",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QualityFormats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Conditions = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityFormats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpgradeProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Tiers = table.Column<string>(type: "TEXT", nullable: false),
                    Cutoff = table.Column<int>(type: "INTEGER", nullable: false),
                    UpgradesEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinScoreDelta = table.Column<int>(type: "INTEGER", nullable: false),
                    UpgradeUntilScore = table.Column<int>(type: "INTEGER", nullable: false),
                    FormatScores = table.Column<string>(type: "TEXT", nullable: false),
                    PageTolerancePercent = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowReplacingUnknown = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpgradeProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Series_UpgradeProfileId",
                table: "Series",
                column: "UpgradeProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityFormats_Name",
                table: "QualityFormats",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeProfiles_Name",
                table: "UpgradeProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Series_UpgradeProfiles_UpgradeProfileId",
                table: "Series",
                column: "UpgradeProfileId",
                principalTable: "UpgradeProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Series_UpgradeProfiles_UpgradeProfileId",
                table: "Series");

            migrationBuilder.DropTable(
                name: "QualityFormats");

            migrationBuilder.DropTable(
                name: "UpgradeProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Series_UpgradeProfileId",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "UpgradeProfileId",
                table: "Series");
        }
    }
}
