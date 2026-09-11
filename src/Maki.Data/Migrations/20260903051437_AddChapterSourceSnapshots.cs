using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChapterSourceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ChapterSnapshotAt",
                table: "SourceMappings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChapterSourceLinks",
                columns: table => new
                {
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceMappingId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceChapterId = table.Column<string>(type: "TEXT", nullable: false),
                    NumberRaw = table.Column<string>(type: "TEXT", nullable: true),
                    Volume = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterSourceLinks", x => new { x.ChapterId, x.SourceMappingId });
                    table.ForeignKey(
                        name: "FK_ChapterSourceLinks_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChapterSourceLinks_SourceMappings_SourceMappingId",
                        column: x => x.SourceMappingId,
                        principalTable: "SourceMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterSourceLinks_SourceMappingId",
                table: "ChapterSourceLinks",
                column: "SourceMappingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChapterSourceLinks");

            migrationBuilder.DropColumn(
                name: "ChapterSnapshotAt",
                table: "SourceMappings");
        }
    }
}
