using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class PageConversionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PageConversionHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: true),
                    PassId = table.Column<string>(type: "TEXT", nullable: true),
                    TrashPath = table.Column<string>(type: "TEXT", nullable: true),
                    TrashBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    BytesBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceFormat = table.Column<string>(type: "TEXT", nullable: true),
                    ResultFormat = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UndoneAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageConversionHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PageConversionHistory_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageConversionHistory_SeriesId_UndoneAtUtc",
                table: "PageConversionHistory",
                columns: new[] { "SeriesId", "UndoneAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageConversionHistory");
        }
    }
}
