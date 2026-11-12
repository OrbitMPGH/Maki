using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class TorrentStallClocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastPeerSeenAt",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastProgressAt",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastPeerSeenAt",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "LastProgressAt",
                table: "DownloadQueue");
        }
    }
}
