using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class PageConversionQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConversionQueuedAtUtc",
                table: "ChapterFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChapterFiles_ConversionQueuedAtUtc",
                table: "ChapterFiles",
                column: "ConversionQueuedAtUtc",
                filter: "ConversionQueuedAtUtc IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChapterFiles_ConversionQueuedAtUtc",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "ConversionQueuedAtUtc",
                table: "ChapterFiles");
        }
    }
}
