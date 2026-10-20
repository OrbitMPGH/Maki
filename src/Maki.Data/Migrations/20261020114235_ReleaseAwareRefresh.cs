using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseAwareRefresh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnnouncedNextReleaseAt",
                table: "SourceMappings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckAt",
                table: "Series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextCheckAt",
                table: "Series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeenAt",
                table: "ChapterSourceLinks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Series_NextCheckAt",
                table: "Series",
                column: "NextCheckAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Series_NextCheckAt",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "AnnouncedNextReleaseAt",
                table: "SourceMappings");

            migrationBuilder.DropColumn(
                name: "LastCheckAt",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "NextCheckAt",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "FirstSeenAt",
                table: "ChapterSourceLinks");
        }
    }
}
