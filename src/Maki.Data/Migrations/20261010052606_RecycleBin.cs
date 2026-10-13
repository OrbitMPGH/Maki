using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecycleBin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecycleBin",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RootFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    RootPath = table.Column<string>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalPath = table.Column<string>(type: "TEXT", nullable: false),
                    BinPath = table.Column<string>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesTitle = table.Column<string>(type: "TEXT", nullable: false),
                    ChapterFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    FileJson = table.Column<string>(type: "TEXT", nullable: true),
                    ChaptersJson = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeletedByName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecycleBin", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecycleBin_DeletedAtUtc",
                table: "RecycleBin",
                column: "DeletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RecycleBin_SeriesId",
                table: "RecycleBin",
                column: "SeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecycleBin");
        }
    }
}
