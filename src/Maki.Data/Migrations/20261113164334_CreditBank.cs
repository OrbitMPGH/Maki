using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreditBank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BankSeen",
                table: "ChapterCredits",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PageCount",
                table: "ChapterCredits",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CreditBank",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Tail = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceChapterId = table.Column<int>(type: "INTEGER", nullable: true),
                    Strip = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignatureFirst = table.Column<int>(type: "INTEGER", nullable: false),
                    Signature = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Hits = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditBank", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreditBank_Chapters_SourceChapterId",
                        column: x => x.SourceChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CreditBank_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditBank_SeriesId",
                table: "CreditBank",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditBank_SourceChapterId",
                table: "CreditBank",
                column: "SourceChapterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditBank");

            migrationBuilder.DropColumn(
                name: "BankSeen",
                table: "ChapterCredits");

            migrationBuilder.DropColumn(
                name: "PageCount",
                table: "ChapterCredits");
        }
    }
}
