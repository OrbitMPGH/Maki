using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChapterUpgrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UpgradeInfoJson",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReplacedAtUtc",
                table: "ChapterFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceChapterId",
                table: "ChapterFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Trusted",
                table: "ChapterFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "UpgradeAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceMappingId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceChapterId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProfileVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Probed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CandidatePageCount = table.Column<int>(type: "INTEGER", nullable: true),
                    CandidateWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    CandidateScore = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpgradeAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpgradeAttempts_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UpgradeAttempts_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UpgradeAttempts_SourceMappings_SourceMappingId",
                        column: x => x.SourceMappingId,
                        principalTable: "SourceMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UpgradeHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChapterFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    QueueItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProfileVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    QueuedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: false),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: false),
                    TrashPath = table.Column<string>(type: "TEXT", nullable: true),
                    TrashBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevertedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpgradeHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpgradeHistory_ChapterFiles_ChapterFileId",
                        column: x => x.ChapterFileId,
                        principalTable: "ChapterFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UpgradeHistory_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UpgradeHistory_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeAttempts_ChapterId_SourceMappingId_SourceChapterId_ProfileId_ProfileVersion",
                table: "UpgradeAttempts",
                columns: new[] { "ChapterId", "SourceMappingId", "SourceChapterId", "ProfileId", "ProfileVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeAttempts_SeriesId",
                table: "UpgradeAttempts",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeAttempts_SourceMappingId",
                table: "UpgradeAttempts",
                column: "SourceMappingId");

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeHistory_ChapterFileId",
                table: "UpgradeHistory",
                column: "ChapterFileId");

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeHistory_ChapterId",
                table: "UpgradeHistory",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeHistory_SeriesId_CreatedAtUtc",
                table: "UpgradeHistory",
                columns: new[] { "SeriesId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpgradeAttempts");

            migrationBuilder.DropTable(
                name: "UpgradeHistory");

            migrationBuilder.DropColumn(
                name: "UpgradeInfoJson",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "ReplacedAtUtc",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "SourceChapterId",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "Trusted",
                table: "ChapterFiles");
        }
    }
}
