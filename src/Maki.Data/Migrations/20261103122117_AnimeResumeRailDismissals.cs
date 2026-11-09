using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnimeResumeRailDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ListUpdatedAtUtc",
                table: "AnimeSignals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AnimeResumeRailDismissals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MangaBakaId = table.Column<int>(type: "INTEGER", nullable: false),
                    CoveredTo = table.Column<double>(type: "REAL", nullable: true),
                    DismissedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeResumeRailDismissals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnimeResumeRailDismissals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnimeResumeRailDismissals_UserId_MangaBakaId",
                table: "AnimeResumeRailDismissals",
                columns: new[] { "UserId", "MangaBakaId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimeResumeRailDismissals");

            migrationBuilder.DropColumn(
                name: "ListUpdatedAtUtc",
                table: "AnimeSignals");
        }
    }
}
