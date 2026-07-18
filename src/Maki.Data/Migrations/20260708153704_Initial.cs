using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppConfig",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppConfig", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ChapterFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", nullable: false),
                    DateAdded = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReleaseHash = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NamingConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesFolderFormat = table.Column<string>(type: "TEXT", nullable: false),
                    ChapterFileFormat = table.Column<string>(type: "TEXT", nullable: false),
                    ChapterFileFormatWithVolume = table.Column<string>(type: "TEXT", nullable: false),
                    RenameChapters = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NamingConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RootFolders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RootFolders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    SortTitle = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    MangaBakaId = table.Column<int>(type: "INTEGER", nullable: true),
                    AniListId = table.Column<int>(type: "INTEGER", nullable: true),
                    MalId = table.Column<int>(type: "INTEGER", nullable: true),
                    MangaUpdatesId = table.Column<string>(type: "TEXT", nullable: true),
                    MangaDexUuid = table.Column<string>(type: "TEXT", nullable: true),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    MonitorNewItems = table.Column<int>(type: "INTEGER", nullable: false),
                    RootFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    FolderName = table.Column<string>(type: "TEXT", nullable: false),
                    CoverPath = table.Column<string>(type: "TEXT", nullable: true),
                    TotalChapters = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalVolumes = table.Column<int>(type: "INTEGER", nullable: true),
                    AuthorStory = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorArt = table.Column<string>(type: "TEXT", nullable: true),
                    Added = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastMetadataRefresh = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Series_RootFolders_RootFolderId",
                        column: x => x.RootFolderId,
                        principalTable: "RootFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Chapters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<decimal>(type: "TEXT", precision: 9, scale: 3, nullable: true),
                    NumberRaw = table.Column<string>(type: "TEXT", nullable: true),
                    Volume = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    IsOneShot = table.Column<bool>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChapterFileId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Chapters_ChapterFiles_ChapterFileId",
                        column: x => x.ChapterFileId,
                        principalTable: "ChapterFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Chapters_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", nullable: false),
                    SourceSeriesId = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    LanguageFilter = table.Column<string>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastRefresh = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceMappings_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DownloadQueue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceMappingId = table.Column<int>(type: "INTEGER", nullable: true),
                    Protocol = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleaseInfoJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PagesTotal = table.Column<int>(type: "INTEGER", nullable: false),
                    PagesDone = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttempt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadQueue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadQueue_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DownloadQueue_SourceMappings_SourceMappingId",
                        column: x => x.SourceMappingId,
                        principalTable: "SourceMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterFiles_SeriesId",
                table: "ChapterFiles",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_ChapterFileId",
                table: "Chapters",
                column: "ChapterFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_SeriesId_Number_Volume_Language",
                table: "Chapters",
                columns: new[] { "SeriesId", "Number", "Volume", "Language" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_ChapterId",
                table: "DownloadQueue",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_SourceMappingId",
                table: "DownloadQueue",
                column: "SourceMappingId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_Status",
                table: "DownloadQueue",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Series_MangaBakaId",
                table: "Series",
                column: "MangaBakaId");

            migrationBuilder.CreateIndex(
                name: "IX_Series_RootFolderId",
                table: "Series",
                column: "RootFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Series_SortTitle",
                table: "Series",
                column: "SortTitle");

            migrationBuilder.CreateIndex(
                name: "IX_SourceMappings_SeriesId_SourceName",
                table: "SourceMappings",
                columns: new[] { "SeriesId", "SourceName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppConfig");

            migrationBuilder.DropTable(
                name: "DownloadQueue");

            migrationBuilder.DropTable(
                name: "NamingConfigs");

            migrationBuilder.DropTable(
                name: "Chapters");

            migrationBuilder.DropTable(
                name: "SourceMappings");

            migrationBuilder.DropTable(
                name: "ChapterFiles");

            migrationBuilder.DropTable(
                name: "Series");

            migrationBuilder.DropTable(
                name: "RootFolders");
        }
    }
}
