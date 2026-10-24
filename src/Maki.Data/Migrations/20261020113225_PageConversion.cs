using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class PageConversion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConversionCheckedAtUtc",
                table: "ChapterFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ConversionSavedBytes",
                table: "ChapterFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "OriginalImageFormat",
                table: "ChapterFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChapterFiles_ConversionCheckedAtUtc",
                table: "ChapterFiles",
                column: "ConversionCheckedAtUtc",
                filter: "ConversionCheckedAtUtc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChapterFiles_ConversionCheckedAtUtc",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "ConversionCheckedAtUtc",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "ConversionSavedBytes",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "OriginalImageFormat",
                table: "ChapterFiles");
        }
    }
}
