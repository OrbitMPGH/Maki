using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChapterCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChapterCredits",
                columns: table => new
                {
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChapterFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    EdgeKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReferencesKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    HeadPages = table.Column<int>(type: "INTEGER", nullable: false),
                    HeadOffset = table.Column<double>(type: "REAL", nullable: false),
                    TailPages = table.Column<int>(type: "INTEGER", nullable: false),
                    DetectorVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    TextVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    OcrVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Judge = table.Column<int>(type: "INTEGER", nullable: false),
                    CandidatesJson = table.Column<string>(type: "TEXT", nullable: true),
                    Skipped = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProbeHit = table.Column<bool>(type: "INTEGER", nullable: true),
                    Failed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScannedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterCredits", x => x.ChapterId);
                    table.ForeignKey(
                        name: "FK_ChapterCredits_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChapterCredits_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterCredits_SeriesId",
                table: "ChapterCredits",
                column: "SeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChapterCredits");
        }
    }
}
