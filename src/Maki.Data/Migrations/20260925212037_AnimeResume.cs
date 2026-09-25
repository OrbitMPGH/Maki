using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnimeResume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AnimeResumeDismissedAt",
                table: "UserSeriesStates",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EndDate",
                table: "AnimeSignals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Episodes",
                table: "AnimeSignals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "AnimeSignals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Progress",
                table: "AnimeSignals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "AnimeSignals",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnimeResumeDismissedAt",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "AnimeSignals");

            migrationBuilder.DropColumn(
                name: "Episodes",
                table: "AnimeSignals");

            migrationBuilder.DropColumn(
                name: "Format",
                table: "AnimeSignals");

            migrationBuilder.DropColumn(
                name: "Progress",
                table: "AnimeSignals");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "AnimeSignals");
        }
    }
}
