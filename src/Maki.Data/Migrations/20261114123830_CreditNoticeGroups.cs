using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreditNoticeGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "CreditNotices",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroupSource",
                table: "CreditNotices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Invite",
                table: "CreditNotices",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "CreditNotices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NameVersion",
                table: "CreditNotices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Override",
                table: "CreditNotices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedName",
                table: "CreditNotices",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GroupName",
                table: "CreditNotices");

            migrationBuilder.DropColumn(
                name: "GroupSource",
                table: "CreditNotices");

            migrationBuilder.DropColumn(
                name: "Invite",
                table: "CreditNotices");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "CreditNotices");

            migrationBuilder.DropColumn(
                name: "NameVersion",
                table: "CreditNotices");

            migrationBuilder.DropColumn(
                name: "Override",
                table: "CreditNotices");

            migrationBuilder.DropColumn(
                name: "SuggestedName",
                table: "CreditNotices");
        }
    }
}
