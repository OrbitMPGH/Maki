using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeriesUpgradeScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastUpgradeScanProbed",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpgradeScanQueued",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpgradeScanUtc",
                table: "Series",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUpgradeScanProbed",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "LastUpgradeScanQueued",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "LastUpgradeScanUtc",
                table: "Series");
        }
    }
}
