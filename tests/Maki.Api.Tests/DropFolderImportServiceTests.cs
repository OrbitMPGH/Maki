using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Sources;
using Maki.Core.Tests;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The drop folder poll: an entry is touched only after it held still across two polls, one that
/// names exactly one library series is imported into it, and anything else stays where it is and
/// is listed for a manual import.
/// </summary>
public class DropFolderImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly DropFolderState _state = new();
    private readonly RecordingInbox _inbox = new();
    private readonly string _base =
        Path.Combine(Path.GetTempPath(), "maki-drop-folder-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string _drop;

    public DropFolderImportServiceTests()
    {
        _root = Path.Combine(_base, "library");
        _drop = Path.Combine(_base, "drop");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_drop);
        _settings.Set(SettingKeys.ImportDropFolderEnabled, "true");
        _settings.Set(SettingKeys.ImportDropFolderPath, _drop);
        _settings.Set(SettingKeys.ImportDropFolderPlacement, "move");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    private static void WriteZip(string path, string content = "page")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("001.png").Open());
        writer.Write(content);
    }

    private void SeedSeries(params string[] titles)
    {
        using var db = _db.NewContext();
        var root = db.RootFolders.FirstOrDefault() ?? db.RootFolders.Add(new RootFolder { Path = _root }).Entity;
        db.SaveChanges();
        foreach (var title in titles)
        {
            var series = new Series { Title = title, SortTitle = title, FolderName = title + db.Series.Count(), RootFolderId = root.Id };
            db.Series.Add(series);
            db.SaveChanges();
            db.Chapters.AddRange(
                new Chapter { SeriesId = series.Id, Number = 1, Language = "en" },
                new Chapter { SeriesId = series.Id, Number = 2, Language = "en" });
            db.SaveChanges();
        }
    }

    private async Task PollAsync()
    {
        await using var db = _db.NewContext();
        var scans = new KavitaScanService(
            new KavitaClient(new StubHttpClientFactory("{}")), _settings, _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance);
        var archives = new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance);
        var registry = new SourceRegistry([]);
        var linker = new CbzLinkService(
            db, registry, scans, new StatsEventService(db), archives, new SourceAvailability(_settings, registry),
            TestQuality.Create(registry), _settings, NullLogger<CbzLinkService>.Instance);
        var rename = new SeriesRenameService(
            db, new NamingService(_settings), scans, new TestLocalizer(), NullLogger<SeriesRenameService>.Instance);
        var importer = new TorrentImportService(
            db, null!, null!, linker, rename, archives, _settings,
            new UpgradeEvaluationService(db, TestQuality.Create(registry)), _inbox,
            NullLogger<TorrentImportService>.Instance);
        var manual = new ManualImportService(db, importer, new TestCurrentUser(0, authenticated: false),
            NullLogger<ManualImportService>.Instance);
        var service = new DropFolderImportService(
            db, _settings, manual, _state, _inbox, NullLogger<DropFolderImportService>.Instance);
        await service.TickAsync(force: true, default);
    }

    private async Task<int> LinkedChaptersAsync()
    {
        await using var db = _db.NewContext();
        return await db.Chapters.CountAsync(c => c.ChapterFileId != null);
    }

    [Fact]
    public async Task A_matching_entry_is_imported_on_the_second_poll_and_moved_out()
    {
        SeedSeries("Vagabond");
        WriteZip(Path.Combine(_drop, "Vagabond v01 (Digital)", "Vagabond 001.cbz"));
        WriteZip(Path.Combine(_drop, "Vagabond v01 (Digital)", "Vagabond 002.cbz"));

        await PollAsync();
        Assert.Equal(0, await LinkedChaptersAsync());

        await PollAsync();

        Assert.Equal(2, await LinkedChaptersAsync());
        Assert.False(Directory.Exists(Path.Combine(_drop, "Vagabond v01 (Digital)")));
        await using var db = _db.NewContext();
        Assert.Equal(QueueStatus.Completed, (await db.DownloadQueue.SingleAsync()).Status);
        Assert.Single(_inbox.Raised);
    }

    [Fact]
    public async Task An_entry_that_changes_between_polls_waits()
    {
        SeedSeries("Vagabond");
        var file = Path.Combine(_drop, "Vagabond 001.cbz");
        WriteZip(file);
        await PollAsync();
        File.Delete(file);
        WriteZip(file, "a bigger page than before");

        await PollAsync();
        Assert.Equal(0, await LinkedChaptersAsync());

        await PollAsync();
        Assert.Equal(1, await LinkedChaptersAsync());
    }

    [Fact]
    public async Task Unmatched_and_ambiguous_entries_stay_and_are_listed()
    {
        SeedSeries("Monster", "Monster");
        WriteZip(Path.Combine(_drop, "Monster 001.cbz"));
        WriteZip(Path.Combine(_drop, "Pluto 001.cbz"));

        await PollAsync();
        await PollAsync();

        Assert.True(File.Exists(Path.Combine(_drop, "Monster 001.cbz")));
        Assert.True(File.Exists(Path.Combine(_drop, "Pluto 001.cbz")));
        Assert.Equal(
            [("Monster 001.cbz", DropFolderPendingReason.Ambiguous), ("Pluto 001.cbz", DropFolderPendingReason.Unmatched)],
            _state.Pending.Select(p => (p.Name, p.Reason)));
        Assert.Equal(0, await LinkedChaptersAsync());
    }

    [Fact]
    public async Task A_copied_entry_is_imported_once_and_left_alone_afterwards()
    {
        _settings.Set(SettingKeys.ImportDropFolderPlacement, "copy");
        SeedSeries("Vagabond");
        WriteZip(Path.Combine(_drop, "Vagabond 001.cbz"));

        await PollAsync();
        await PollAsync();
        await PollAsync();

        Assert.True(File.Exists(Path.Combine(_drop, "Vagabond 001.cbz")));
        Assert.Equal(1, await LinkedChaptersAsync());
        Assert.Empty(_state.Pending);
        await using var db = _db.NewContext();
        Assert.Single(db.DownloadQueue);
    }

    [Fact]
    public void Names_drop_the_span_and_tags()
    {
        Assert.Contains("vagabond", DropFolderImportService.NamesOf("Vagabond v01-03 (Digital) (Group)", isFile: false));
        Assert.Contains("monster", DropFolderImportService.NamesOf("Monster 012.cbz", isFile: true));
    }
}
