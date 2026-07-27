using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExternalChapterReadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "External",
                table: "ChapterProgress",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UnreadAt",
                table: "ChapterProgress",
                type: "TEXT",
                nullable: true);

            // Rows the old Kavita read-status import wrote are recognisable: it left PageCount at 0
            // rather than opening every archive in the library, while the reader always stores the
            // real slice length on the write that completes a chapter. Flagging them keeps the
            // chapter table honest about which reads Maki actually observed.
            migrationBuilder.Sql(
                """
                UPDATE "ChapterProgress" SET "External" = 1
                WHERE "Completed" = 1 AND "PageCount" = 0
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "External",
                table: "ChapterProgress");

            migrationBuilder.DropColumn(
                name: "UnreadAt",
                table: "ChapterProgress");
        }
    }
}
