using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Maki.Api.Configuration;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Metadata;
using Maki.Core.Naming;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace Maki.Api.Tests;

/// <summary>
/// Undoing a library import: rows, the series it created and the CBZs it built go, the folder moves
/// back, and every file the user had before the import is left exactly as it was. Refused, and
/// changing nothing, when the series has been downloaded into or read since.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class LibraryImportUndoTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly SourceMatchQueue _queue = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-import-undo-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "maki-import-undo-config-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _priorConfigDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");

    public LibraryImportUndoTests()
    {
        // CoverService.DeleteCover resolves under the config dir; never let it reach a real one.
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorConfigDir);
        _db.Dispose();
        foreach (var dir in new[] { _root, _configDir })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private static void WriteZip(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("001.png").Open());
        writer.Write("page " + Path.GetFileName(path));
    }

    private static void WriteTar(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = WriterFactory.OpenWriter(stream, ArchiveType.Tar, new WriterOptions(CompressionType.None));
        writer.Write("001.png", new MemoryStream(Encoding.UTF8.GetBytes("tar page")), DateTime.UtcNow);
    }

    private void WriteFixture(string folder)
    {
        WriteZip(At(folder, "Chainsaw Man 001.cbz"));
        WriteZip(At(folder, "Chainsaw Man 002.zip"));
        Directory.CreateDirectory(At(folder, "Chainsaw Man 003"));
        File.WriteAllText(At(folder, "Chainsaw Man 003", "001.png"), "loose page");
        WriteTar(At(folder, "Chainsaw Man 004.cbz"));
        File.WriteAllText(At(folder, "notes.txt"), "the user's own notes");
    }

    /// <summary>Every file under the folder with a hash of its bytes.</summary>
    private static Dictionary<string, string> Fingerprint(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(dir, f),
                f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private CbzLinkService LinkService(MakiDbContext db)
    {
        var registry = new SourceRegistry([]);
        return new CbzLinkService(
            db,
            registry,
            new KavitaScanService(
                new KavitaClient(new StubHttpClientFactory("{}")),
                _settings,
                _db.ScopeFactory(),
                NullLogger<KavitaScanService>.Instance),
            new StatsEventService(db),
            new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            new SourceAvailability(_settings, registry),
            TestQuality.Create(registry),
            _settings,
            NullLogger<CbzLinkService>.Instance);
    }

    private LibraryImportService ImportService(MakiDbContext db) => new(
        db, [new FixedProvider()], null!, LinkService(db), _queue,
        new EventBroadcaster(new NoopHubContext(), _db.ScopeFactory()),
        _settings, new NamingService(_settings), new StatsEventService(db),
        new SeriesIdentityService(db, NullLogger<SeriesIdentityService>.Instance), new TestLocalizer(),
        new TestCurrentUser(1), NullLogger<LibraryImportService>.Instance);

    private LibraryImportUndoService UndoService(MakiDbContext db, ICurrentUser? user = null) => new(
        db, _queue, new CoverService(null!, new AppPaths(), _settings, NullLogger<CoverService>.Instance),
        new StatsEventService(db), new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        new MangaBakaLocalStore(
            new MangaBakaDumpOptions("", _configDir), _settings, NullLogger<MangaBakaLocalStore>.Instance),
        new TestLocalizer(), user ?? new TestCurrentUser(1), NullLogger<LibraryImportUndoService>.Instance);

    private SourceMatchWorkerHostedService Worker()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddSingleton<EventBroadcaster>(
            sp => new EventBroadcaster(new NoopHubContext(), sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddScoped(sp => LinkService(sp.GetRequiredService<MakiDbContext>()));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new SourceMatchWorkerHostedService(_queue, scopes, NullLogger<SourceMatchWorkerHostedService>.Instance);
    }

    private int SeedRoot()
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        return root.Id;
    }

    private async Task<ImportResult> ImportAsync(
        int rootId, string folder, string batch = "batch-1", string providerId = "42")
    {
        await using var db = _db.NewContext();
        var result = await ImportService(db).ImportAsync(
            rootId, new ImportRequestItem(folder, providerId), operationId: batch);
        Assert.True(result.Success, result.Error);
        return result;
    }

    private async Task<int> RecordIdAsync()
    {
        await using var db = _db.NewContext();
        return (await db.ImportBatchFolders.SingleAsync()).Id;
    }

    private async Task<ImportUndoOutcome> UndoAsync(int recordId, ICurrentUser? user = null)
    {
        await using var db = _db.NewContext();
        return (await UndoService(db, user).UndoFolderAsync(recordId, CancellationToken.None))!;
    }

    [Fact]
    public async Task Undo_removes_what_the_import_made_and_leaves_every_original_file_alone()
    {
        var rootId = SeedRoot();
        WriteFixture("chainsaw raws");
        var originals = Fingerprint(At("chainsaw raws"));

        var result = await ImportAsync(rootId, "chainsaw raws");
        Assert.NotEqual("chainsaw raws", result.NewFolderName);
        Assert.True(File.Exists(At(result.NewFolderName!, "Chainsaw Man 002.cbz")));
        Assert.True(File.Exists(At(result.NewFolderName!, "Chainsaw Man 004.cbt")));

        var outcome = await UndoAsync(await RecordIdAsync());

        Assert.True(outcome.Undone, outcome.Error);
        Assert.Null(outcome.Warnings);
        Assert.False(Directory.Exists(At(result.NewFolderName!)));
        // The folder holds exactly what it held before, byte for byte: the mislabelled original is
        // back under its own name and the CBZs Maki built are gone.
        Assert.Equal(originals, Fingerprint(At("chainsaw raws")));

        await using var check = _db.NewContext();
        Assert.Empty(check.Series);
        Assert.Empty(check.ChapterFiles);
        Assert.NotNull((await check.ImportBatchFolders.SingleAsync()).UndoneAt);
    }

    [Fact]
    public async Task Undo_cancels_the_deferred_match_and_link_so_neither_runs_afterwards()
    {
        var rootId = SeedRoot();
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));
        var result = await ImportAsync(rootId, "chainsaw raws");
        var seriesId = result.SeriesId!.Value;
        await using (var db = _db.NewContext())
        {
            var series = await db.Series.SingleAsync();
            Assert.True(series.SourceMatchPending);
            Assert.NotEqual(PendingImportLink.None, series.PendingImportLink);
        }

        var outcome = await UndoAsync(await RecordIdAsync());
        Assert.True(outcome.Undone, outcome.Error);

        // The match the import queued is still in the channel; reading it now finds nothing to do.
        await Worker().MatchAsync(seriesId, default);
        await Worker().LinkAsync(seriesId, default);
        Assert.False(_queue.LinkReader.TryRead(out _));
        await using var check = _db.NewContext();
        Assert.Empty(check.Series);
    }

    [Fact]
    public async Task Undo_waits_for_a_running_match_and_refuses_when_it_does_not_finish_in_time()
    {
        var rootId = SeedRoot();
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));
        var result = await ImportAsync(rootId, "chainsaw raws");
        var seriesId = result.SeriesId!.Value;
        while (_queue.TryReadMatch(out _))
        {
        }

        var running = new TaskCompletionSource();
        Assert.True(_queue.TryBeginMatch(seriesId, running.Task));
        var wait = LibraryImportUndoService.MatchWait;
        LibraryImportUndoService.MatchWait = TimeSpan.FromMilliseconds(100);
        try
        {
            var refused = await UndoAsync(await RecordIdAsync());

            Assert.False(refused.Undone);
            Assert.StartsWith("error.libraryImport.undoMatchRunning", refused.Error);
            await using (var check = _db.NewContext())
            {
                // Put back as it was and queued again, so the import still completes.
                var series = await check.Series.SingleAsync();
                Assert.True(series.SourceMatchPending);
                Assert.Equal(PendingImportLink.LinkAndComicInfo, series.PendingImportLink);
                Assert.True(_queue.TryReadMatch(out var requeued));
                Assert.Equal(seriesId, requeued);
            }

            _queue.EndMatch(seriesId);
            running.SetResult();
            var outcome = await UndoAsync(await RecordIdAsync());
            Assert.True(outcome.Undone, outcome.Error);
        }
        finally
        {
            LibraryImportUndoService.MatchWait = wait;
        }
    }

    [Fact]
    public async Task Undo_is_refused_and_changes_nothing_once_a_chapter_was_downloaded_into_the_series()
    {
        var rootId = SeedRoot();
        WriteFixture("chainsaw raws");
        var result = await ImportAsync(rootId, "chainsaw raws");
        await using (var db = _db.NewContext())
        {
            db.ChapterFiles.Add(new ChapterFile
            {
                SeriesId = result.SeriesId!.Value,
                RelativePath = Path.Combine(result.NewFolderName!, "Chainsaw Man 005.cbz"),
                SourceName = "mangadex",
            });
            await db.SaveChangesAsync();
        }

        var after = Fingerprint(At(result.NewFolderName!));

        var outcome = await UndoAsync(await RecordIdAsync());

        Assert.False(outcome.Undone);
        Assert.StartsWith("error.libraryImport.undoDownloadedSince", outcome.Error);
        Assert.Equal(after, Fingerprint(At(result.NewFolderName!)));
        await using var check = _db.NewContext();
        Assert.Single(check.Series);
        Assert.Null((await check.ImportBatchFolders.SingleAsync()).UndoneAt);
    }

    [Fact]
    public async Task Undo_is_refused_when_somebody_read_the_series_since()
    {
        var rootId = SeedRoot();
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));
        var result = await ImportAsync(rootId, "chainsaw raws");
        await using (var db = _db.NewContext())
        {
            var chapter = new Chapter { SeriesId = result.SeriesId!.Value, Number = 1, Language = "en" };
            db.Chapters.Add(chapter);
            await db.SaveChangesAsync();
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1, SeriesId = chapter.SeriesId, ChapterId = chapter.Id, PageIndex = 3, PageCount = 20,
                StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var outcome = await UndoAsync(await RecordIdAsync());

        Assert.False(outcome.Undone);
        Assert.StartsWith("error.libraryImport.undoHasProgress", outcome.Error);
        Assert.True(Directory.Exists(At(result.NewFolderName!)));
        await using var check = _db.NewContext();
        Assert.Single(check.Series);
    }

    [Fact]
    public async Task Undo_of_an_import_into_an_existing_series_keeps_the_series_and_moves_merged_files_back()
    {
        var rootId = SeedRoot();
        int seriesId;
        await using (var db = _db.NewContext())
        {
            var series = new Series
            {
                Title = "Chainsaw Man", SortTitle = "Chainsaw Man", FolderName = "Chainsaw Man",
                RootFolderId = rootId, MangaBakaId = 42,
            };
            db.Series.Add(series);
            await db.SaveChangesAsync();
            db.Chapters.AddRange(Enumerable.Range(1, 2).Select(n =>
                new Chapter { SeriesId = series.Id, Number = n, Language = "en" }));
            await db.SaveChangesAsync();
            seriesId = series.Id;
        }

        // The series' own folder exists (made when it was added), so the import merges into it.
        Directory.CreateDirectory(At("Chainsaw Man"));
        WriteZip(At("cm raws", "Chainsaw Man 001.cbz"));
        WriteZip(At("cm raws", "Chainsaw Man 002.zip"));
        var originals = Fingerprint(At("cm raws"));

        ImportResult result;
        await using (var db = _db.NewContext())
        {
            result = await ImportService(db).ImportAsync(
                rootId, new ImportRequestItem("cm raws", "42"), updateComicInfo: false, operationId: "batch-1");
        }

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(At("cm raws")));
        await using (var db = _db.NewContext())
        {
            Assert.Equal(2, await db.Chapters.CountAsync(c => c.SeriesId == seriesId && c.ChapterFileId != null));
        }

        var outcome = await UndoAsync(await RecordIdAsync());

        Assert.True(outcome.Undone, outcome.Error);
        Assert.Equal(originals, Fingerprint(At("cm raws")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("Chainsaw Man")));
        await using var check = _db.NewContext();
        var kept = await check.Series.SingleAsync();
        Assert.Equal(seriesId, kept.Id);
        Assert.Equal("Chainsaw Man", kept.FolderName);
        Assert.Empty(check.ChapterFiles);
        Assert.Equal(0, await check.Chapters.CountAsync(c => c.ChapterFileId != null));
        Assert.Equal(2, await check.Chapters.CountAsync());
    }

    [Fact]
    public async Task A_batch_is_listed_undone_as_a_whole_and_refused_a_second_time()
    {
        var rootId = SeedRoot();
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));
        await ImportAsync(rootId, "chainsaw raws");

        await using (var db = _db.NewContext())
        {
            var batch = Assert.Single(await UndoService(db).RecentAsync(rootId, CancellationToken.None));
            Assert.Equal("batch-1", batch.BatchId);
            var folder = Assert.Single(batch.Folders);
            Assert.Equal("chainsaw raws", folder.OriginalFolderName);
            Assert.True(folder.CreatedSeries);
            Assert.True(folder.LinkPending);
            Assert.Equal(1, folder.FileCount);

            var outcomes = await UndoService(db).UndoBatchAsync("batch-1", CancellationToken.None);
            Assert.True(Assert.Single(outcomes!).Undone);
            Assert.Null(await UndoService(db).UndoBatchAsync("no-such-batch", CancellationToken.None));
        }

        var again = await UndoAsync(await RecordIdAsync());
        Assert.False(again.Undone);
        Assert.StartsWith("error.libraryImport.undoAlreadyDone", again.Error);
    }

    [Fact]
    public async Task Keep_original_folder_imports_undo_without_moving_anything()
    {
        _settings.Set(SettingKeys.LibraryFolderNamingMode, FolderNamingMode.KeepOriginal);
        var rootId = SeedRoot();
        WriteFixture("chainsaw raws");
        var originals = Fingerprint(At("chainsaw raws"));
        var result = await ImportAsync(rootId, "chainsaw raws");
        Assert.Equal("chainsaw raws", result.NewFolderName);

        var outcome = await UndoAsync(await RecordIdAsync());

        Assert.True(outcome.Undone, outcome.Error);
        Assert.Equal(originals, Fingerprint(At("chainsaw raws")));
    }

    [Fact]
    public async Task A_built_CBZ_whose_loose_pages_were_deleted_since_is_kept_as_the_only_copy()
    {
        var rootId = SeedRoot();
        // Pages at the folder root: the folder itself is the source, and it never stops existing.
        Directory.CreateDirectory(At("loose raws"));
        File.WriteAllText(At("loose raws", "001.png"), "page one");
        File.WriteAllText(At("loose raws", "002.png"), "page two");
        var result = await ImportAsync(rootId, "loose raws");
        // Named after the folder it was built in, which by then has the series' name.
        var builtName = result.NewFolderName + ".cbz";
        Assert.True(File.Exists(At(result.NewFolderName!, builtName)));
        File.Delete(At(result.NewFolderName!, "002.png"));

        var outcome = await UndoAsync(await RecordIdAsync());

        Assert.True(outcome.Undone, outcome.Error);
        Assert.Contains(outcome.Warnings!, w => w.StartsWith("error.libraryImport.undoKeptBuilt"));
        Assert.True(File.Exists(At("loose raws", builtName)));
        Assert.True(File.Exists(At("loose raws", "001.png")));
    }

    [Fact]
    public async Task A_match_that_is_still_running_gets_its_flags_back_and_no_early_link()
    {
        var rootId = SeedRoot();
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));
        var result = await ImportAsync(rootId, "chainsaw raws");
        var seriesId = result.SeriesId!.Value;
        while (_queue.TryReadMatch(out _))
        {
        }

        // The running match has already saved its mappings and is now syncing chapters.
        await using (var db = _db.NewContext())
        {
            db.SourceMappings.Add(new SourceMapping
            {
                SeriesId = seriesId, SourceName = "mangadex", SourceSeriesId = "x", Url = "https://example.invalid/x",
            });
            await db.SaveChangesAsync();
        }

        var running = new TaskCompletionSource();
        Assert.True(_queue.TryBeginMatch(seriesId, running.Task));
        var wait = LibraryImportUndoService.MatchWait;
        LibraryImportUndoService.MatchWait = TimeSpan.FromMilliseconds(100);
        try
        {
            var refused = await UndoAsync(await RecordIdAsync());

            Assert.StartsWith("error.libraryImport.undoMatchRunning", refused.Error);
            await using var check = _db.NewContext();
            var series = await check.Series.SingleAsync();
            Assert.True(series.SourceMatchPending);
            Assert.Equal(PendingImportLink.LinkAndComicInfo, series.PendingImportLink);
            Assert.False(_queue.LinkReader.TryRead(out _));
        }
        finally
        {
            LibraryImportUndoService.MatchWait = wait;
            _queue.EndMatch(seriesId);
            running.SetResult();
        }
    }

    [Fact]
    public async Task Undo_refuses_to_rename_back_onto_a_name_another_series_owns()
    {
        var rootId = SeedRoot();
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));
        var result = await ImportAsync(rootId, "chainsaw raws");
        // A keep-new-standard series points at a folder that does not exist on disk yet.
        await using (var db = _db.NewContext())
        {
            db.Series.Add(new Series
            {
                Title = "Other", SortTitle = "Other", FolderName = "chainsaw raws", RootFolderId = rootId,
            });
            await db.SaveChangesAsync();
        }

        var outcome = await UndoAsync(await RecordIdAsync());

        Assert.False(outcome.Undone);
        Assert.StartsWith("error.libraryImport.folderOwnedByOtherSeries", outcome.Error);
        Assert.True(Directory.Exists(At(result.NewFolderName!)));
        Assert.False(Directory.Exists(At("chainsaw raws")));
    }

    [Fact]
    public async Task A_failed_save_puts_the_pending_match_back_and_a_batch_keeps_going()
    {
        var rootId = SeedRoot();
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));
        WriteZip(At("monster raws", "Monster 001.cbz"));
        await ImportAsync(rootId, "chainsaw raws");
        await ImportAsync(rootId, "monster raws", providerId: "43");
        while (_queue.TryReadMatch(out _))
        {
        }

        var options = new DbContextOptionsBuilder<MakiDbContext>(_db.Options)
            .AddInterceptors(new FailingSave())
            .Options;
        await using (var failing = new MakiDbContext(options))
        {
            var outcomes = await UndoService(failing).UndoBatchAsync("batch-1", CancellationToken.None);

            Assert.Equal(2, outcomes!.Count);
            Assert.All(outcomes, o => Assert.StartsWith("error.libraryImport.undoFailed", o.Error));
        }

        await using var check = _db.NewContext();
        Assert.All(await check.Series.ToListAsync(), s =>
        {
            Assert.True(s.SourceMatchPending);
            Assert.Equal(PendingImportLink.LinkAndComicInfo, s.PendingImportLink);
        });
        Assert.All(await check.ImportBatchFolders.ToListAsync(), r => Assert.Null(r.UndoneAt));
        Assert.True(_queue.TryReadMatch(out _));
    }

    [Fact]
    public async Task Removing_a_created_series_needs_DeleteSeries_unless_the_caller_ran_the_import()
    {
        var rootId = SeedRoot();
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));
        await ImportAsync(rootId, "chainsaw raws");
        var id = await RecordIdAsync();

        var stranger = await UndoAsync(id, new TestCurrentUser(2, permissions: MakiPermission.ImportLibrary));
        Assert.False(stranger.Undone);
        Assert.StartsWith("error.libraryImport.undoNeedsDeleteSeries", stranger.Error);

        var importer = await UndoAsync(id, new TestCurrentUser(1, permissions: MakiPermission.ImportLibrary));
        Assert.True(importer.Undone, importer.Error);

        // Recorded the way SeriesController.Delete records a removal.
        await using var check = _db.NewContext();
        var removed = await check.StatsEvents.SingleAsync(e => e.Type == StatsEventType.SeriesRemoved);
        Assert.Contains("\"providerId\":\"42\"", removed.PayloadJson);
    }

    [Fact]
    public async Task Undo_refuses_when_the_series_files_were_renamed_since()
    {
        var rootId = SeedRoot();
        WriteFixture("chainsaw raws");
        var result = await ImportAsync(rootId, "chainsaw raws");
        var folder = result.NewFolderName!;
        File.Move(At(folder, "Chainsaw Man 002.cbz"), At(folder, "Chainsaw Man - Ch.2.cbz"));
        await using (var db = _db.NewContext())
        {
            var row = await db.ChapterFiles.SingleAsync(f => f.RelativePath.EndsWith("Chainsaw Man 002.cbz"));
            row.RelativePath = Path.Combine(folder, "Chainsaw Man - Ch.2.cbz");
            await db.SaveChangesAsync();
        }

        var after = Fingerprint(At(folder));

        var outcome = await UndoAsync(await RecordIdAsync());

        Assert.False(outcome.Undone);
        Assert.StartsWith("error.libraryImport.undoFilesRenamed", outcome.Error);
        Assert.Equal(after, Fingerprint(At(folder)));
    }

    [Fact]
    public async Task Two_undos_at_once_undo_once_and_the_second_says_so()
    {
        _settings.Set(SettingKeys.LibraryFolderNamingMode, FolderNamingMode.KeepOriginal);
        var rootId = SeedRoot();
        WriteZip(At("chainsaw raws", "Chainsaw Man 001.cbz"));
        var result = await ImportAsync(rootId, "chainsaw raws");
        var id = await RecordIdAsync();

        Task<ImportUndoOutcome> first, second;
        using (await SeriesLocks.SeriesAsync(result.SeriesId!.Value, CancellationToken.None))
        {
            first = Task.Run(() => UndoAsync(id));
            second = Task.Run(() => UndoAsync(id));
            await Task.Delay(300);
        }

        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, o => o.Undone);
        Assert.Single(outcomes, o => o.Error?.StartsWith("error.libraryImport.undoAlreadyDone") == true);
    }

    [Fact]
    public async Task A_folder_that_could_not_be_moved_back_can_be_retried()
    {
        var rootId = SeedRoot();
        WriteFixture("chainsaw raws");
        var originals = Fingerprint(At("chainsaw raws"));
        var result = await ImportAsync(rootId, "chainsaw raws");
        // A file under the old name: not a folder, so the undo starts, and the move back then fails.
        File.WriteAllText(At("chainsaw raws"), "in the way");

        var first = await UndoAsync(await RecordIdAsync());

        Assert.True(first.Undone, first.Error);
        Assert.Contains(first.Warnings!, w => w.StartsWith("error.libraryImport.undoDiskPending"));
        Assert.True(Directory.Exists(At(result.NewFolderName!)));
        await using (var db = _db.NewContext())
        {
            var folder = Assert.Single(Assert.Single(await UndoService(db).RecentAsync(rootId, default)).Folders);
            Assert.True(folder.DiskPending);
        }

        File.Delete(At("chainsaw raws"));
        var retry = await UndoAsync(await RecordIdAsync());

        Assert.True(retry.Undone, retry.Error);
        Assert.Null(retry.Warnings);
        Assert.Equal(originals, Fingerprint(At("chainsaw raws")));
        await using var check = _db.NewContext();
        Assert.False(ImportOperations.Parse((await check.ImportBatchFolders.SingleAsync()).OperationsJson).DiskPending);
    }

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("the database refused the save");
    }

    private sealed class FixedProvider : IMetadataProvider
    {
        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId, Title = "Chainsaw Man", MangaBakaId = int.Parse(providerId),
            });
    }
}
