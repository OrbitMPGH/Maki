using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Naming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Applying the naming formats to a series already on disk. The formats themselves are covered by
/// NamingFormatterTests; what matters here is that the folder, the files and the rows they're
/// recorded in all end up agreeing, and that the guards refuse the cases that would leave them
/// disagreeing.
/// </summary>
public class SeriesRenameServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-rename-" + Guid.NewGuid().ToString("N")[..8]);

    public SeriesRenameServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SeriesRenameService Service()
    {
        var scans = new KavitaScanService(
            new KavitaClient(new StubHttpClientFactory("{}")),
            _settings,
            _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance);

        return new SeriesRenameService(
            _db.NewContext(),
            new NamingService(_settings),
            scans,
            NullLogger<SeriesRenameService>.Instance);
    }

    /// <summary>Seeds a series with one chapter per number, and a real file on disk for each.</summary>
    private int SeedSeries(
        string title, string folderName, int? year = 1989,
        params (decimal Number, int? Volume, string Language)[] chapters)
    {
        using var db = _db.NewContext();
        var rootFolder = new RootFolder { Path = _root };
        db.RootFolders.Add(rootFolder);
        db.SaveChanges();

        var series = new Series
        {
            Title = title,
            SortTitle = title,
            Year = year,
            FolderName = folderName,
            RootFolderId = rootFolder.Id
        };
        db.Series.Add(series);
        db.SaveChanges();

        Directory.CreateDirectory(Path.Combine(_root, folderName));

        foreach (var (number, volume, language) in chapters)
        {
            var chapter = new Chapter
            {
                SeriesId = series.Id,
                Number = number,
                Volume = volume,
                Language = language
            };
            db.Chapters.Add(chapter);
            db.SaveChanges();

            var relative = Path.Combine(folderName,
                FileNameBuilder.BuildChapterFileName(series, chapter));
            File.WriteAllText(Path.Combine(_root, relative), "cbz");

            var file = new ChapterFile
            {
                SeriesId = series.Id,
                RelativePath = relative,
                Size = 3,
                SourceName = "test",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();

            chapter.ChapterFileId = file.Id;
            db.SaveChanges();
        }

        return series.Id;
    }

    [Fact]
    public async Task Plan_reports_the_folder_the_default_format_wants()
    {
        var id = SeedSeries("Berserk", "Berserk", chapters: (24m, 3, "en"));

        var plan = await Service().PlanAsync(id, CancellationToken.None);

        Assert.NotNull(plan);
        Assert.Equal("Berserk", plan!.FolderFrom);
        Assert.Equal("Berserk (1989)", plan.FolderTo);
        Assert.True(plan.FolderChanged);
        Assert.Single(plan.Files);
        Assert.Equal(Path.Combine("Berserk (1989)", "Berserk Vol.3 Ch.24.cbz"), plan.Files[0].To);
    }

    [Fact]
    public async Task Plan_has_nothing_to_do_when_the_names_already_match()
    {
        var id = SeedSeries("Berserk", "Berserk (1989)", chapters: (24m, 3, "en"));

        var plan = await Service().PlanAsync(id, CancellationToken.None);

        Assert.False(plan!.HasChanges);
        Assert.Empty(plan.Files);
    }

    [Fact]
    public async Task Rename_moves_the_folder_and_updates_the_rows()
    {
        var id = SeedSeries("Berserk", "Berserk", chapters: [(24m, 3, "en"), (25m, 3, "en")]);

        var result = await Service().RenameAsync(id, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);

        Assert.False(Directory.Exists(Path.Combine(_root, "Berserk")));
        Assert.True(File.Exists(Path.Combine(_root, "Berserk (1989)", "Berserk Vol.3 Ch.24.cbz")));

        using var db = _db.NewContext();
        Assert.Equal("Berserk (1989)", db.Series.Single(s => s.Id == id).FolderName);
        Assert.All(db.ChapterFiles.Where(f => f.SeriesId == id).ToList(),
            f => Assert.StartsWith("Berserk (1989)", f.RelativePath));
    }

    [Fact]
    public async Task Rename_follows_a_changed_chapter_format()
    {
        _settings.Set(SettingKeys.LibraryChapterFormat, "{Series Title} - {Chapter Number:000}");
        var id = SeedSeries("Berserk", "Berserk (1989)", chapters: (24m, 3, "en"));

        var result = await Service().RenameAsync(id, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.True(File.Exists(Path.Combine(_root, "Berserk (1989)", "Berserk - 024.cbz")));
        Assert.False(File.Exists(Path.Combine(_root, "Berserk (1989)", "Berserk Vol.3 Ch.24.cbz")));

        using var db = _db.NewContext();
        Assert.Equal(Path.Combine("Berserk (1989)", "Berserk - 024.cbz"),
            db.ChapterFiles.Single(f => f.SeriesId == id).RelativePath);
    }

    [Fact]
    public async Task Rename_is_refused_while_a_download_is_in_flight()
    {
        var id = SeedSeries("Berserk", "Berserk", chapters: (24m, 3, "en"));

        using (var db = _db.NewContext())
        {
            db.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = id,
                ChapterId = db.Chapters.First(c => c.SeriesId == id).Id,
                Status = QueueStatus.Downloading
            });
            db.SaveChanges();
        }

        var result = await Service().RenameAsync(id, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Contains("active download", result.Error);
        Assert.True(Directory.Exists(Path.Combine(_root, "Berserk")));
    }

    [Fact]
    public async Task Rename_is_refused_when_two_chapters_want_one_name()
    {
        // Same chapter in two languages, and a format with no {Chapter Language} to tell them apart.
        var id = SeedSeries("Berserk", "Berserk (1989)", chapters: (24m, 3, "en"));

        using (var db = _db.NewContext())
        {
            var chapter = new Chapter { SeriesId = id, Number = 24m, Volume = 3, Language = "es" };
            db.Chapters.Add(chapter);
            db.SaveChanges();

            var file = new ChapterFile
            {
                SeriesId = id,
                RelativePath = Path.Combine("Berserk (1989)", "Berserk Vol.3 Ch.24 [es].cbz"),
                Size = 3,
                SourceName = "test",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            chapter.ChapterFileId = file.Id;
            db.SaveChanges();
        }

        var result = await Service().RenameAsync(id, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Contains("same file name", result.Error);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task Rename_refuses_to_merge_into_an_existing_folder()
    {
        var id = SeedSeries("Berserk", "Berserk", chapters: (24m, 3, "en"));
        Directory.CreateDirectory(Path.Combine(_root, "Berserk (1989)"));

        var result = await Service().RenameAsync(id, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Contains("already exists", result.Error);
    }

    [Fact]
    public async Task Rename_is_a_no_op_when_nothing_changes()
    {
        var id = SeedSeries("Berserk", "Berserk (1989)", chapters: (24m, 3, "en"));

        var result = await Service().RenameAsync(id, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.True(File.Exists(Path.Combine(_root, "Berserk (1989)", "Berserk Vol.3 Ch.24.cbz")));
    }

    [Fact]
    public async Task Missing_file_still_gets_its_row_repointed()
    {
        _settings.Set(SettingKeys.LibraryChapterFormat, "{Series Title} - {Chapter Number:000}");
        var id = SeedSeries("Berserk", "Berserk (1989)", chapters: (24m, 3, "en"));
        File.Delete(Path.Combine(_root, "Berserk (1989)", "Berserk Vol.3 Ch.24.cbz"));

        var result = await Service().RenameAsync(id, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Contains(result.Warnings, w => w.Contains("missing from disk"));

        // The row has to follow the format either way: leaving it pointing at a name nothing will
        // ever be written under is worse than recording where the file should be.
        using var db = _db.NewContext();
        Assert.Equal(Path.Combine("Berserk (1989)", "Berserk - 024.cbz"),
            db.ChapterFiles.Single(f => f.SeriesId == id).RelativePath);
    }

    [Fact]
    public async Task Folder_only_rename_repoints_files_it_did_not_have_to_touch()
    {
        var id = SeedSeries("Berserk", "Berserk", chapters: (24m, 3, "en"));

        var result = await Service().RenameAsync(id, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Empty(result.Warnings);

        using var db = _db.NewContext();
        Assert.Equal(Path.Combine("Berserk (1989)", "Berserk Vol.3 Ch.24.cbz"),
            db.ChapterFiles.Single(f => f.SeriesId == id).RelativePath);
    }
}
