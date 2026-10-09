using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class DataIndexCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthEvents_Timestamp",
                table: "AuthEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuthEvents_UserId",
                table: "AuthEvents");

            // The index was created by raw SQL in OpdsKeyOneLivePerScope; it is now modelled, so
            // re-create it through EF.
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_UserApiKeys_Opds_Live_UserId;");

            migrationBuilder.CreateIndex(
                name: "IX_UserApiKeys_Opds_Live_UserId",
                table: "UserApiKeys",
                columns: new[] { "UserId", "Scope" },
                unique: true,
                filter: "RevokedAt IS NULL AND Scope = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AuthEvents_Timestamp",
                table: "AuthEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuthEvents_UserId",
                table: "AuthEvents",
                column: "UserId");
        }
    }
}
