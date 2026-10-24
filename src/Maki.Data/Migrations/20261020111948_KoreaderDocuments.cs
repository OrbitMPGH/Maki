using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class KoreaderDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KoreaderDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Digest = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FileVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KoreaderDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KoreaderDocuments_ChapterFiles_ChapterFileId",
                        column: x => x.ChapterFileId,
                        principalTable: "ChapterFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KoreaderDocuments_ChapterFileId_Kind_Digest",
                table: "KoreaderDocuments",
                columns: new[] { "ChapterFileId", "Kind", "Digest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KoreaderDocuments_Digest",
                table: "KoreaderDocuments",
                column: "Digest");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KoreaderDocuments");
        }
    }
}
