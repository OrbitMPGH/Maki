using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChapterFileRemovedAndCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FileRemovedAt",
                table: "Chapters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "ChapterProgress",
                type: "TEXT",
                nullable: true);

            // UpdatedAt is the closest record of when an existing read finished. It can only be
            // later than the real completion, so anything timed off it errs towards waiting longer.
            migrationBuilder.Sql("UPDATE ChapterProgress SET CompletedAt = UpdatedAt WHERE Completed = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileRemovedAt",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "ChapterProgress");
        }
    }
}
