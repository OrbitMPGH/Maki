using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class TorrentClientRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientRemovalClient",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ClientRemovalDeleteData",
                table: "DownloadQueue",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ClientRemovalMode",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClientRemovalPendingAt",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClientRemovedAt",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_ClientRemovalPendingAt",
                table: "DownloadQueue",
                column: "ClientRemovalPendingAt",
                filter: "\"ClientRemovalPendingAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_ClientRemovalPendingAt",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "ClientRemovalClient",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "ClientRemovalDeleteData",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "ClientRemovalMode",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "ClientRemovalPendingAt",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "ClientRemovedAt",
                table: "DownloadQueue");
        }
    }
}
