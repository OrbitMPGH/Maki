using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// The 1 to 100 quality numbers give way to calibrated stops. The active format's quality maps to
    /// the nearest stop (a tie goes to the higher one), and PNG pages stay lossless as they were until
    /// an admin confirms the new settings. Nothing is re-queued: <c>ConversionCheckedAtUtc</c> stays.
    /// </summary>
    public partial class PageConversionStops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "AppConfig" WHERE "Key" IN ('library.pageconversion.stop', 'library.pageconversion.pnglossless')
                    AND EXISTS (SELECT 1 FROM "AppConfig" WHERE "Key" = 'library.pageconversion.format'
                                AND LOWER(TRIM("Value")) IN ('webp', 'avif'));

                INSERT INTO "AppConfig" ("Key", "Value")
                SELECT 'library.pageconversion.stop',
                    CASE
                        WHEN f.Format = 'webp' THEN
                            CASE WHEN f.Q < 60 THEN 'smallest' WHEN f.Q < 75 THEN 'compact' WHEN f.Q < 85 THEN 'balanced' ELSE 'high' END
                        ELSE
                            CASE WHEN f.Q < 55 THEN 'smallest' WHEN f.Q < 63 THEN 'compact' WHEN f.Q < 73 THEN 'balanced' ELSE 'high' END
                    END
                FROM (
                    SELECT LOWER(TRIM(c."Value")) AS Format,
                        COALESCE(
                            (SELECT CAST(q."Value" AS INTEGER) FROM "AppConfig" q
                             WHERE q."Key" = 'library.pageconversion.' || LOWER(TRIM(c."Value")) || 'quality'
                               AND CAST(q."Value" AS INTEGER) BETWEEN 1 AND 100),
                            CASE WHEN LOWER(TRIM(c."Value")) = 'webp' THEN 80 ELSE 55 END) AS Q
                    FROM "AppConfig" c
                    WHERE c."Key" = 'library.pageconversion.format' AND LOWER(TRIM(c."Value")) IN ('webp', 'avif')
                ) f;

                INSERT INTO "AppConfig" ("Key", "Value")
                SELECT 'library.pageconversion.pnglossless', 'true'
                FROM "AppConfig"
                WHERE "Key" = 'library.pageconversion.format' AND LOWER(TRIM("Value")) IN ('webp', 'avif');

                DELETE FROM "AppConfig" WHERE "Key" IN ('library.pageconversion.webpquality', 'library.pageconversion.avifquality');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "AppConfig" WHERE "Key" IN ('library.pageconversion.stop', 'library.pageconversion.pnglossless');
                """);
        }
    }
}
