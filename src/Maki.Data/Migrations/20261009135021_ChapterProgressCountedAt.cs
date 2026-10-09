using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChapterProgressCountedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CountedAt",
                table: "ChapterProgress",
                type: "TEXT",
                nullable: true);

            // One-shots already counted before the column existed: without this the next read after a
            // mark-unread would count them once more.
            migrationBuilder.Sql(
                """
                UPDATE ChapterProgress SET CountedAt = COALESCE(CompletedAt, UpdatedAt)
                WHERE Completed = 1 AND Watched = 0 AND External = 0 AND BulkMarked = 0 AND PageCount > 0
                  AND ChapterId IN (SELECT Id FROM Chapters WHERE Number IS NULL)
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CountedAt",
                table: "ChapterProgress");
        }
    }
}
