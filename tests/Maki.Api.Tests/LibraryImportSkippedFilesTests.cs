using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A comic the import could not place used to vanish: the page said "Imported" and the file sat in
/// the folder linked to nothing. The release fixture has both shapes, a truncated Berserk Ch.002
/// and a Dandadan Ch.2.5 no source lists.
/// </summary>
public class LibraryImportSkippedFilesTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "maki-import-skipped-" + Guid.NewGuid().ToString("N")[..8], "Series");

    public LibraryImportSkippedFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _db.Dispose();
        var parent = Path.GetDirectoryName(_dir)!;
        if (Directory.Exists(parent))
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private LibraryImportService Service(Maki.Data.MakiDbContext db) => new(
        db, [], null!, null!, null!, null!, null!, null!, null!, null!, null!, new TestLocalizer(),
        new TestCurrentUser(1), new RecordingNotifications(), new TestUserLocaleResolver(), new TestLocalizer(),
        NullLogger<LibraryImportService>.Instance);

    private string WriteZip(string name)
    {
        var path = Path.Combine(_dir, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("001.jpg").Open());
        writer.Write("page");
        return path;
    }

    [Fact]
    public async Task Unreadable_and_unmatched_files_are_reported_with_their_reason()
    {
        var linkedFile = WriteZip("Series Ch.1.cbz");
        WriteZip("Series Ch.2.5.cbz");
        WriteZip("Extras.cbz");
        var truncated = File.ReadAllBytes(WriteZip("Series Ch.2.cbz"));
        File.WriteAllBytes(Path.Combine(_dir, "Series Ch.2.cbz"), truncated[..(truncated.Length / 2)]);

        await using var db = _db.NewContext();
        var root = new RootFolder { Path = Path.GetDirectoryName(_dir)! };
        db.RootFolders.Add(root);
        var series = new Series { Title = "Series", SortTitle = "series", FolderName = "Series", RootFolder = root };
        db.Series.Add(series);
        await db.SaveChangesAsync();
        var file = new ChapterFile { SeriesId = series.Id, RelativePath = Path.Combine("Series", "Series Ch.1.cbz"), SourceName = "import" };
        db.ChapterFiles.Add(file);
        await db.SaveChangesAsync();
        db.Chapters.Add(new Chapter { SeriesId = series.Id, Number = 1, NumberRaw = "1", ChapterFileId = file.Id });
        await db.SaveChangesAsync();

        var service = Service(db);
        var (files, unreadable) = service.MaterializeComics(_dir);
        var skipped = await service.SkippedFilesAsync(series.Id, _dir, files, unreadable, CancellationToken.None);

        Assert.Contains(linkedFile, files);
        Assert.Equal(
            [
                new ImportSkippedFile("Extras.cbz", ImportSkipReason.Unrecognized),
                new ImportSkippedFile("Series Ch.2.5.cbz", ImportSkipReason.NoMatchingChapter),
                new ImportSkippedFile("Series Ch.2.cbz", ImportSkipReason.Unreadable)
            ],
            skipped);
    }

    [Fact]
    public void A_second_copy_of_a_found_comic_is_not_reported_unreadable()
    {
        WriteZip("Series Ch.1.cbz");
        File.WriteAllText(Path.Combine(_dir, "Series Ch.1.cbr"), "not really a rar");

        using var db = _db.NewContext();
        var (_, unreadable) = Service(db).MaterializeComics(_dir);

        Assert.Empty(unreadable);
    }
}
