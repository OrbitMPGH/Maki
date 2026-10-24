using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameDownloadClientSettings : Migration
    {
        private static readonly (string Old, string New)[] Renames =
        [
            ("qbittorrent.url", "downloadclient.url"),
            ("qbittorrent.username", "downloadclient.username"),
            ("qbittorrent.password", "downloadclient.password"),
            ("qbittorrent.category", "downloadclient.label"),
            ("qbittorrent.pathmapfrom", "downloadclient.pathmapfrom"),
            ("qbittorrent.pathmapto", "downloadclient.pathmapto"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (from, to) in Renames)
            {
                Rename(migrationBuilder, from, to);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (from, to) in Renames)
            {
                Rename(migrationBuilder, to, from);
            }

            migrationBuilder.Sql("""DELETE FROM "AppConfig" WHERE "Key" = 'downloadclient.kind';""");
        }

        // A target row that already exists loses to the source row, which is the one the previous release read.
        private static void Rename(MigrationBuilder migrationBuilder, string from, string to)
        {
            migrationBuilder.Sql($"""
                DELETE FROM "AppConfig" WHERE "Key" = '{to}' AND EXISTS (SELECT 1 FROM "AppConfig" WHERE "Key" = '{from}');
                UPDATE "AppConfig" SET "Key" = '{to}' WHERE "Key" = '{from}';
                """);
        }
    }
}
