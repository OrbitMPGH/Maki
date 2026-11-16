using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class RatingDecimalAndBreakdown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "Rating",
                table: "UserSeriesStates",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "ProvisionalRating",
                table: "UserSeriesStates",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RatingBreakdownJson",
                table: "UserSeriesStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "Rating",
                table: "RatingSnapshots",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "BreakdownJson",
                table: "RatingSnapshots",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RatingBreakdownJson",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "BreakdownJson",
                table: "RatingSnapshots");

            migrationBuilder.AlterColumn<int>(
                name: "Rating",
                table: "UserSeriesStates",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ProvisionalRating",
                table: "UserSeriesStates",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Rating",
                table: "RatingSnapshots",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");
        }
    }
}
