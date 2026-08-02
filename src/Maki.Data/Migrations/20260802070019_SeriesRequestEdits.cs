using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeriesRequestEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EditedAt",
                table: "SeriesRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EditedByUserId",
                table: "SeriesRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OriginalChapterEnd",
                table: "SeriesRequests",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OriginalChapterStart",
                table: "SeriesRequests",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeriesRequests_EditedByUserId",
                table: "SeriesRequests",
                column: "EditedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_SeriesRequests_AspNetUsers_EditedByUserId",
                table: "SeriesRequests",
                column: "EditedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SeriesRequests_AspNetUsers_EditedByUserId",
                table: "SeriesRequests");

            migrationBuilder.DropIndex(
                name: "IX_SeriesRequests_EditedByUserId",
                table: "SeriesRequests");

            migrationBuilder.DropColumn(
                name: "EditedAt",
                table: "SeriesRequests");

            migrationBuilder.DropColumn(
                name: "EditedByUserId",
                table: "SeriesRequests");

            migrationBuilder.DropColumn(
                name: "OriginalChapterEnd",
                table: "SeriesRequests");

            migrationBuilder.DropColumn(
                name: "OriginalChapterStart",
                table: "SeriesRequests");
        }
    }
}
