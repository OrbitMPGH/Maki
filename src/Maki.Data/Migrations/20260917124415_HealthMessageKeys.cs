using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class HealthMessageKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MessageKey",
                table: "HealthHistory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParamsJson",
                table: "HealthHistory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessageKey",
                table: "HealthFindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParamsJson",
                table: "HealthFindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessageKey",
                table: "HealthChecks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParamsJson",
                table: "HealthChecks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MessageKey",
                table: "HealthHistory");

            migrationBuilder.DropColumn(
                name: "ParamsJson",
                table: "HealthHistory");

            migrationBuilder.DropColumn(
                name: "MessageKey",
                table: "HealthFindings");

            migrationBuilder.DropColumn(
                name: "ParamsJson",
                table: "HealthFindings");

            migrationBuilder.DropColumn(
                name: "MessageKey",
                table: "HealthChecks");

            migrationBuilder.DropColumn(
                name: "ParamsJson",
                table: "HealthChecks");
        }
    }
}
