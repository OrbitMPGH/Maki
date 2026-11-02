using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProvisionalRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ProvisionalAtChapter",
                table: "UserSeriesStates",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProvisionalRatedAt",
                table: "UserSeriesStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProvisionalRating",
                table: "UserSeriesStates",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProvisionalAtChapter",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "ProvisionalRatedAt",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "ProvisionalRating",
                table: "UserSeriesStates");
        }
    }
}
