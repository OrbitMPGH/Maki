using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChapterFileQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "ChapterSourceLinks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "ChapterFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageFormat",
                table: "ChapterFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MeasuredAtUtc",
                table: "ChapterFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MedianHeight",
                table: "ChapterFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MedianWidth",
                table: "ChapterFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PageCount",
                table: "ChapterFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Tier",
                table: "ChapterFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ChapterFiles_MeasuredAtUtc",
                table: "ChapterFiles",
                column: "MeasuredAtUtc",
                filter: "MeasuredAtUtc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChapterFiles_MeasuredAtUtc",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "Group",
                table: "ChapterSourceLinks");

            migrationBuilder.DropColumn(
                name: "Group",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "ImageFormat",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "MeasuredAtUtc",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "MedianHeight",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "MedianWidth",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "PageCount",
                table: "ChapterFiles");

            migrationBuilder.DropColumn(
                name: "Tier",
                table: "ChapterFiles");
        }
    }
}
