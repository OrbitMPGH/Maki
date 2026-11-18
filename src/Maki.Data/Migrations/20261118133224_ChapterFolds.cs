using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChapterFolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Parts",
                table: "ChapterSourceLinks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChapterFolds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceMappingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    Floor = table.Column<double>(type: "REAL", nullable: false),
                    PartsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FoldedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterFolds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChapterFolds_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChapterFolds_SourceMappings_SourceMappingId",
                        column: x => x.SourceMappingId,
                        principalTable: "SourceMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterFolds_SeriesId",
                table: "ChapterFolds",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterFolds_SourceMappingId_Language_Floor",
                table: "ChapterFolds",
                columns: new[] { "SourceMappingId", "Language", "Floor" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChapterFolds");

            migrationBuilder.DropColumn(
                name: "Parts",
                table: "ChapterSourceLinks");
        }
    }
}
