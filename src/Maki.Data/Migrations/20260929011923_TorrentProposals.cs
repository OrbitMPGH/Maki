using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class TorrentProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UpgradeHistory_ChapterFiles_ChapterFileId",
                table: "UpgradeHistory");

            migrationBuilder.AddColumn<string>(
                name: "DetailJson",
                table: "UpgradeHistory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "UpgradeHistory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastVolumeSearchUtc",
                table: "Series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TorrentProposals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleaseGuid = table.Column<string>(type: "TEXT", nullable: false),
                    ReleaseInfoJson = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Indexer = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    SpanJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonsJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpgradeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AlreadyMetCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SkippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MissingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UnknownCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    QueueItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TorrentProposals_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeHistory_GroupId",
                table: "UpgradeHistory",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TorrentProposals_SeriesId_ReleaseGuid",
                table: "TorrentProposals",
                columns: new[] { "SeriesId", "ReleaseGuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TorrentProposals_SeriesId_Status",
                table: "TorrentProposals",
                columns: new[] { "SeriesId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TorrentProposals");

            migrationBuilder.DropIndex(
                name: "IX_UpgradeHistory_GroupId",
                table: "UpgradeHistory");

            migrationBuilder.DropColumn(
                name: "DetailJson",
                table: "UpgradeHistory");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "UpgradeHistory");

            migrationBuilder.DropColumn(
                name: "LastVolumeSearchUtc",
                table: "Series");

            migrationBuilder.AddForeignKey(
                name: "FK_UpgradeHistory_ChapterFiles_ChapterFileId",
                table: "UpgradeHistory",
                column: "ChapterFileId",
                principalTable: "ChapterFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
