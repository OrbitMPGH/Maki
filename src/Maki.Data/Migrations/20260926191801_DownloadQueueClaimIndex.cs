using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class DownloadQueueClaimIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_Protocol_Status_SortOrder_QueuedAt",
                table: "DownloadQueue",
                columns: new[] { "Protocol", "Status", "SortOrder", "QueuedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_Protocol_Status_SortOrder_QueuedAt",
                table: "DownloadQueue");
        }
    }
}
