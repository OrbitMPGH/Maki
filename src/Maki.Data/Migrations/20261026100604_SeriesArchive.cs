using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeriesArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "Series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArchivedByUserId",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Series_ArchivedAt",
                table: "Series",
                column: "ArchivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Series_ArchivedByUserId",
                table: "Series",
                column: "ArchivedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Series_AspNetUsers_ArchivedByUserId",
                table: "Series",
                column: "ArchivedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql("UPDATE AspNetUsers SET Permissions = Permissions | 4096 WHERE (Permissions & 4) != 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Series_AspNetUsers_ArchivedByUserId",
                table: "Series");

            migrationBuilder.DropIndex(
                name: "IX_Series_ArchivedAt",
                table: "Series");

            migrationBuilder.DropIndex(
                name: "IX_Series_ArchivedByUserId",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "ArchivedByUserId",
                table: "Series");
        }
    }
}
