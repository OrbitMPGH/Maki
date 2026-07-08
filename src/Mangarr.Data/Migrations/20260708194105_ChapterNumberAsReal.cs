using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mangarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChapterNumberAsReal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "Number",
                table: "Chapters",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "TEXT",
                oldPrecision: 9,
                oldScale: 3,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "Number",
                table: "Chapters",
                type: "TEXT",
                precision: 9,
                scale: 3,
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);
        }
    }
}
