using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>Repointing a root folder: the dry run's counts, what refuses, and what the save rewrites.</summary>
public sealed class RootFolderPathServiceTests : IDisposable
{
    private readonly TestDb _db = new TestDb().WithDistinctSeriesIds();
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "maki-rootpath-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _old;
    private readonly string _new;

    public RootFolderPathServiceTests()
    {
        _old = Path.Combine(_tmp, "old");
        _new = Path.Combine(_tmp, "new");
        Directory.CreateDirectory(_old);
        Directory.CreateDirectory(_new);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RootFolderPathService Service(MakiDbContext db, IAppSettings? settings = null) =>
        new(db, new SeriesBulkOperations(), new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            settings ?? new FakeAppSettings());

    private int SeedRoot(string path)
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = path };
        db.RootFolders.Add(root);
        db.SaveChanges();
        return root.Id;
    }

    private int SeedSeriesIn(int rootId, string folder, bool archived = false) =>
        _db.SeedSeries(folder, configure: s =>
        {
            s.RootFolderId = rootId;
            s.FolderName = folder;
            if (archived)
            {
                s.ArchivedAt = DateTime.UtcNow;
            }
        });

    [Fact]
    public async Task The_dry_run_counts_series_folders_found_and_missing_under_the_new_path()
    {
        var root = SeedRoot(_old);
        SeedSeriesIn(root, "Berserk");
        SeedSeriesIn(root, "Vagabond", archived: true);
        SeedSeriesIn(root, "Monster");
        Directory.CreateDirectory(Path.Combine(_new, "Berserk"));
        Directory.CreateDirectory(Path.Combine(_new, "vagabond"));

        using var db = _db.NewContext();
        var check = (await Service(db).CheckAsync(root, _new + Path.DirectorySeparatorChar, default)).Check!;

        Assert.Equal(_new, check.Path);
        Assert.Equal(3, check.Series);
        Assert.Equal(2, check.FoldersFound);
        Assert.Equal(["Monster"], check.MissingFolders);
        Assert.Empty(check.Busy);
        Assert.Equal(_old, db.RootFolders.Single(r => r.Id == root).Path);
    }

    [Fact]
    public async Task A_path_that_does_not_exist_is_refused()
    {
        var root = SeedRoot(_old);

        using var db = _db.NewContext();
        var outcome = await Service(db).CheckAsync(root, Path.Combine(_tmp, "gone"), default);

        Assert.Equal("error.rootFolder.doesNotExist", outcome.Refusal?.Key);
    }

    [Fact]
    public async Task Another_root_folders_path_is_refused_as_a_conflict()
    {
        var root = SeedRoot(_old);
        SeedRoot(_new);

        using var db = _db.NewContext();
        var outcome = await Service(db).ApplyAsync(root, _new, default);

        Assert.Equal("error.rootFolder.alreadyExists", outcome.Refusal?.Key);
        Assert.True(outcome.Refusal?.Conflict);
        Assert.Equal(_old, db.RootFolders.Single(r => r.Id == root).Path);
    }

    [Fact]
    public async Task An_active_download_in_the_root_blocks_the_save()
    {
        var root = SeedRoot(_old);
        var series = SeedSeriesIn(root, "Berserk");
        using (var seed = _db.NewContext())
        {
            var chapter = new Chapter { SeriesId = series, Number = 1, Language = "en" };
            seed.Chapters.Add(chapter);
            seed.SaveChanges();
            seed.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = series, ChapterId = chapter.Id, Protocol = AcquisitionProtocol.Scraper,
                Status = QueueStatus.Downloading, QueuedAt = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }

        using var db = _db.NewContext();
        var outcome = await Service(db).ApplyAsync(root, _new, default);

        Assert.Equal(["downloads"], outcome.Check!.Busy);
        using var check = _db.NewContext();
        Assert.Equal(_old, check.RootFolders.Single(r => r.Id == root).Path);
    }

    [Fact]
    public async Task Saving_rewrites_the_root_and_its_recycle_bin_entries_only()
    {
        var root = SeedRoot(_old);
        var other = SeedRoot(Path.Combine(_tmp, "other"));
        using (var seed = _db.NewContext())
        {
            seed.RecycleBin.Add(new RecycleBinEntry
            {
                RootFolderId = root, RootPath = _old, RelativePath = "Berserk/c1.cbz",
                OriginalPath = Path.Combine(_old, "Berserk", "c1.cbz"), BinPath = ".maki-trash/bin/1/c1.cbz",
            });
            seed.RecycleBin.Add(new RecycleBinEntry
            {
                RootFolderId = other, RootPath = Path.Combine(_tmp, "other"), RelativePath = "X/c1.cbz",
                OriginalPath = Path.Combine(_tmp, "other", "X", "c1.cbz"), BinPath = ".maki-trash/bin/2/c1.cbz",
            });
            seed.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            var outcome = await Service(db).ApplyAsync(root, _new, default);
            Assert.NotNull(outcome.Check);
        }

        using var check = _db.NewContext();
        Assert.Equal(_new, check.RootFolders.Single(r => r.Id == root).Path);
        var moved = check.RecycleBin.Single(e => e.RootFolderId == root);
        Assert.Equal(_new, moved.RootPath);
        Assert.Equal(Path.Combine(_new, "Berserk", "c1.cbz"), moved.OriginalPath);
        var untouched = check.RecycleBin.Single(e => e.RootFolderId == other);
        Assert.Equal(Path.Combine(_tmp, "other"), untouched.RootPath);
    }

    [Fact]
    public async Task A_kavita_mapping_under_the_old_path_is_flagged()
    {
        var root = SeedRoot(_old);
        var settings = new FakeAppSettings().Set(SettingKeys.KavitaPathMapFrom, _old);

        using var db = _db.NewContext();
        var check = (await Service(db, settings).CheckAsync(root, _new, default)).Check!;

        Assert.Contains("kavitaPathMap", check.Warnings);
    }

    [Theory]
    [InlineData("old/Berserk/c1.cbz", "new/Berserk/c1.cbz")]
    [InlineData("old", "new")]
    [InlineData("older/c1.cbz", null)]
    public void Rebase_only_moves_paths_inside_the_old_root(string path, string? expected)
    {
        string Full(string p) => Path.Combine(_tmp, p.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected is null ? null : Full(expected), RootFolderPathService.Rebase(Full(path), _old, _new));
    }
}
