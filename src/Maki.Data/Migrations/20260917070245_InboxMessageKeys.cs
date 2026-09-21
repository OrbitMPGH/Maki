using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class InboxMessageKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MessageKey",
                table: "UserNotifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParamsJson",
                table: "UserNotifications",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MessageKey",
                table: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "ParamsJson",
                table: "UserNotifications");
        }
    }
}
