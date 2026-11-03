using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class RatingCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastRatingCheckpointAt",
                table: "UserSeriesStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingRatingCheckpoint",
                table: "UserSeriesStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RatingCheckpointsSeen",
                table: "UserSeriesStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RatingPromptDismissals",
                table: "UserSeriesStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRatingCheckpointAt",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "PendingRatingCheckpoint",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "RatingCheckpointsSeen",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "RatingPromptDismissals",
                table: "UserSeriesStates");
        }
    }
}
