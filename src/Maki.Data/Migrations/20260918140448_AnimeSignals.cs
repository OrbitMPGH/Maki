using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnimeSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnimeSignals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Service = table.Column<string>(type: "TEXT", nullable: false),
                    AnimeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Score = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    MangaBakaId = table.Column<long>(type: "INTEGER", nullable: true),
                    AniListMangaId = table.Column<long>(type: "INTEGER", nullable: true),
                    MalMangaId = table.Column<long>(type: "INTEGER", nullable: true),
                    MatchAttemptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeSignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnimeSignals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnimeSignals_UserId_MangaBakaId",
                table: "AnimeSignals",
                columns: new[] { "UserId", "MangaBakaId" });

            migrationBuilder.CreateIndex(
                name: "IX_AnimeSignals_UserId_Service_AnimeId",
                table: "AnimeSignals",
                columns: new[] { "UserId", "Service", "AnimeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimeSignals");
        }
    }
}
