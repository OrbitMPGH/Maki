using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class QueuePriorityDiskHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_Protocol_Status_SortOrder_QueuedAt",
                table: "DownloadQueue");

            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_SortOrder_QueuedAt",
                table: "DownloadQueue");

            migrationBuilder.AddColumn<long>(
                name: "MinFreeBytes",
                table: "RootFolders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "DownloadQueue",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_Priority_SortOrder_QueuedAt",
                table: "DownloadQueue",
                columns: new[] { "Priority", "SortOrder", "QueuedAt" },
                filter: "\"Status\" NOT IN (6, 8)");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_Protocol_Status_Priority_SortOrder_QueuedAt",
                table: "DownloadQueue",
                columns: new[] { "Protocol", "Status", "Priority", "SortOrder", "QueuedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_Priority_SortOrder_QueuedAt",
                table: "DownloadQueue");

            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_Protocol_Status_Priority_SortOrder_QueuedAt",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "MinFreeBytes",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "DownloadQueue");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_Protocol_Status_SortOrder_QueuedAt",
                table: "DownloadQueue",
                columns: new[] { "Protocol", "Status", "SortOrder", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_SortOrder_QueuedAt",
                table: "DownloadQueue",
                columns: new[] { "SortOrder", "QueuedAt" },
                filter: "\"Status\" NOT IN (6, 8)");
        }
    }
}
