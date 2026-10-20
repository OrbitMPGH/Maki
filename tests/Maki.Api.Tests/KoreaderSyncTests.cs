using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace Maki.Api.Tests;

public sealed class KoreaderSyncTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ReadingProgressGate _gate = new();
    private readonly ReaderArchiveCache _archives = new(NullLogger<ReaderArchiveCache>.Instance);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-kosync-tests", Guid.NewGuid().ToString("N"));
    private readonly int _user;
    private readonly string _token;

    public KoreaderSyncTests()
    {
        Directory.CreateDirectory(_root);
        _user = _db.SeedUser("koreader", MakiPermission.UseOpds, allRootFolders: true);
        _token = _db.SeedApiKey(_user, UserApiKeyScope.Opds);
        _db.SetUserConfig(_user, (SettingKeys.OpdsEnabled, "true"));
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class NoScheduler : ISchedulerFactory
    {
        public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IScheduler> GetScheduler(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IScheduler?> GetScheduler(string schedName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>A controller over a context scoped to nobody, which is what a KOReader request gets.</summary>
    private KoreaderSyncController Controller(string? token)
    {
        var nobody = new DataScope();
        nobody.SetNobody();
        var db = new MakiDbContext(_db.Options, nobody);
        var reader = new ReaderService(db, _archives,
            new ReadingProgressService(db, _gate, NullLogger<ReadingProgressService>.Instance),
            InertKavitaPusher.For(_db.ScopeFactory()), new ReadingSessionService(db), NullLogger<ReaderService>.Instance);
        var context = new DefaultHttpContext();
        if (token is not null)
        {
            context.Request.Headers["x-auth-user"] = token;
        }

        return new KoreaderSyncController(
            new OpdsAccessService(db, TimeProvider.System),
            new KoreaderSyncService(db, reader, _cache),
            db,
            new NoScheduler(),
            new TestLocalizer(),
            NullLogger<KoreaderSyncController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private ReaderService Reader(MakiDbContext db) =>
        new(db, _archives, new ReadingProgressService(db, _gate, NullLogger<ReadingProgressService>.Instance),
            InertKavitaPusher.For(_db.ScopeFactory()), new ReadingSessionService(db), NullLogger<ReaderService>.Instance);

    private async Task IndexAsync()
    {
        await using var db = _db.NewContext();
        Assert.True(await new KoreaderDocumentIndexer(db, NullLogger<KoreaderDocumentIndexer>.Instance)
            .RunAsync(CancellationToken.None));
    }

    private string WriteCbz(string name, params string[] entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var stream = archive.CreateEntry(entry).Open();
            stream.WriteByte(0xFF);
        }

        return path;
    }

    private int SeedSeries(string title, Action<Series>? configure = null)
    {
        var seriesId = _db.SeedSeries(title, configure: configure);
        using var db = _db.NewContext();
        var series = db.Series.Include(s => s.RootFolder).First(s => s.Id == seriesId);
        series.RootFolder!.Path = _root;
        series.FolderName = "";
        db.SaveChanges();
        return seriesId;
    }

    private (string Path, Dictionary<decimal, int> Chapters) SeedFile(int seriesId, string name, string[] entries,
        params decimal[] numbers)
    {
        var path = WriteCbz(name, entries);
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId, RelativePath = name, Size = new FileInfo(path).Length,
            SourceName = "test", DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapters = numbers.Select(n => new Chapter
        {
            SeriesId = seriesId, Number = n, Language = "en", ChapterFileId = file.Id
        }).ToList();
        db.Chapters.AddRange(chapters);
        db.SaveChanges();
        return (path, chapters.ToDictionary(c => c.Number!.Value, c => c.Id));
    }

    /// <summary>Three chapters in one volume archive: pages 0-1, 2 and 3-5.</summary>
    private (string Path, Dictionary<decimal, int> Chapters) SeedVolume(string title = "Volume", Action<Series>? configure = null) =>
        SeedFile(SeedSeries(title, configure), $"{title}.cbz",
            [
                "S - c001 - p001.png", "S - c001 - p002.png",
                "S - c002 - p001.png",
                "S - c003 - p001.png", "S - c003 - p002.png", "S - c003 - p003.png"
            ],
            1m, 2m, 3m);

    private static KoreaderProgressRequest Body(string document, string progress, double percentage,
        string device = "Kobo", string deviceId = "kobo-1") =>
        new()
        {
            Document = document,
            Progress = JsonDocument.Parse(JsonSerializer.Serialize(progress)).RootElement,
            Percentage = percentage,
            Device = device,
            DeviceId = deviceId
        };

    private ChapterProgress? Progress(int chapterId)
    {
        using var db = _db.NewContext(_user);
        return db.ChapterProgress.AsNoTracking().SingleOrDefault(p => p.ChapterId == chapterId);
    }

    private List<StatsEvent> ChaptersRead()
    {
        using var db = _db.NewContext();
        return db.StatsEvents.Where(e => e.Type == StatsEventType.ChaptersRead).ToList();
    }

    [Fact]
    public async Task Logging_in_with_the_opds_token_is_authorized()
    {
        Assert.IsType<OkObjectResult>(await Controller(_token).Auth(default));
    }

    [Fact]
    public async Task A_wrong_token_or_a_catalogue_turned_off_is_401()
    {
        Assert.Equal(401, ((ObjectResult)await Controller("nope").Auth(default)).StatusCode);
        Assert.Equal(401, ((ObjectResult)await Controller(null).Auth(default)).StatusCode);

        _db.SetUserConfig(_user, (SettingKeys.OpdsEnabled, "false"));
        Assert.Equal(401, ((ObjectResult)await Controller(_token).Auth(default)).StatusCode);
    }

    [Fact]
    public async Task A_full_api_key_is_not_a_sync_credential()
    {
        var full = _db.SeedApiKey(_user);

        Assert.Equal(401, ((ObjectResult)await Controller(full).Auth(default)).StatusCode);
    }

    [Fact]
    public async Task Register_succeeds_only_with_a_token_as_the_username()
    {
        var ok = (ObjectResult)await Controller(null).Register(new KoreaderRegisterRequest { Username = _token, Password = "x" }, default);
        var refused = (ObjectResult)await Controller(null).Register(new KoreaderRegisterRequest { Username = "me", Password = "x" }, default);

        Assert.Equal(201, ok.StatusCode);
        Assert.Equal(402, refused.StatusCode);
    }

    [Fact]
    public async Task A_push_inside_a_volume_completes_earlier_chapters_and_positions_the_current_one()
    {
        var (path, chapters) = SeedVolume();
        await IndexAsync();

        // KOReader page 5 of 6 is archive page 4, the second page of chapter 3.
        var result = await Controller(_token).Push(Body(KoreaderDigest.PartialMd5(path), "5", 0.8333), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(Progress(chapters[1m])!.Completed);
        Assert.True(Progress(chapters[2m])!.Completed);
        var current = Progress(chapters[3m])!;
        Assert.False(current.Completed);
        Assert.Equal(1, current.PageIndex);
        Assert.Equal(2, ChaptersRead().Count);
    }

    [Fact]
    public async Task The_last_page_completes_a_single_chapter_file()
    {
        var (path, chapters) = SeedFile(SeedSeries("Solo"), "Solo.cbz", ["001.jpg", "002.jpg", "003.jpg"], 7m);
        await IndexAsync();

        await Controller(_token).Push(Body(KoreaderDigest.PartialMd5(path), "3", 1), default);

        Assert.True(Progress(chapters[7m])!.Completed);
    }

    [Fact]
    public async Task A_page_count_that_disagrees_falls_back_to_the_percentage()
    {
        Assert.Equal(4, KoreaderSyncService.ArchivePageFor("5", 5 / 6.0, 6));
        // KOReader counted 12 pages where the archive has 6: page 10 of 12 is about page 5 of 6.
        Assert.Equal(4, KoreaderSyncService.ArchivePageFor("10", 10 / 12.0, 6));
        Assert.Equal(2, KoreaderSyncService.ArchivePageFor("/body/DocFragment[3]", 0.5, 6));
        Assert.Equal(-1, KoreaderSyncService.ArchivePageFor("/body", null, 6));
    }

    [Fact]
    public async Task An_unknown_document_is_accepted_and_writes_nothing()
    {
        var (_, chapters) = SeedVolume();
        await IndexAsync();

        var result = (ObjectResult)await Controller(_token).Push(Body(new string('a', 32), "5", 0.8), default);

        Assert.Equal(202, result.StatusCode);
        Assert.All(chapters.Values, id => Assert.Null(Progress(id)));
    }

    [Fact]
    public async Task A_file_in_a_root_folder_the_user_was_not_granted_is_unknown_to_them()
    {
        using (var db = _db.NewContext())
        {
            db.Users.Single(u => u.Id == _user).AllRootFolders = false;
            db.SaveChanges();
        }

        var (path, chapters) = SeedVolume();
        await IndexAsync();

        var result = (ObjectResult)await Controller(_token).Push(Body(KoreaderDigest.PartialMd5(path), "6", 1), default);

        Assert.Equal(202, result.StatusCode);
        Assert.All(chapters.Values, id => Assert.Null(Progress(id)));
    }

    [Fact]
    public async Task A_pull_reports_where_maki_has_the_reader()
    {
        var (path, chapters) = SeedVolume();
        await IndexAsync();
        await using (var db = _db.NewContext(_user))
        {
            var reader = Reader(db);
            var slice = await reader.SliceAsync(chapters[3m], default);
            await reader.SaveProgressAsync(slice!, 1, null, ReaderService.TimeReport.None, default);
        }

        var result = (OkObjectResult)await Controller(_token).Pull(KoreaderDigest.PartialMd5(path), default);

        var body = Assert.IsType<KoreaderProgressResponse>(result.Value);
        Assert.Equal("5", body.Progress);
        Assert.Equal(0.8333, body.Percentage);
        Assert.Equal(KoreaderSyncService.MakiDevice, body.Device);
    }

    [Fact]
    public async Task A_pull_after_a_push_hands_the_push_back_until_maki_moves()
    {
        var (path, chapters) = SeedVolume();
        await IndexAsync();
        var digest = KoreaderDigest.PartialMd5(path);
        await Controller(_token).Push(Body(digest, "4", 0.6667), default);

        var echoed = Assert.IsType<KoreaderProgressResponse>(((OkObjectResult)await Controller(_token).Pull(digest, default)).Value);
        Assert.Equal(("4", "Kobo", "kobo-1"), (echoed.Progress, echoed.Device, echoed.DeviceId));

        await using (var db = _db.NewContext(_user))
        {
            var reader = Reader(db);
            var slice = await reader.SliceAsync(chapters[3m], default);
            await reader.SaveProgressAsync(slice!, 1, null, ReaderService.TimeReport.None, default);
        }

        var moved = Assert.IsType<KoreaderProgressResponse>(((OkObjectResult)await Controller(_token).Pull(digest, default)).Value);
        Assert.Equal(("5", KoreaderSyncService.MakiDevice), (moved.Progress, moved.Device));
    }

    [Fact]
    public async Task A_pull_with_no_progress_is_an_empty_object()
    {
        var (path, _) = SeedVolume();
        await IndexAsync();

        var result = (OkObjectResult)await Controller(_token).Pull(KoreaderDigest.PartialMd5(path), default);

        Assert.IsNotType<KoreaderProgressResponse>(result.Value);
    }

    [Fact]
    public async Task Filename_matching_resolves_the_name_on_disk()
    {
        var (_, chapters) = SeedFile(SeedSeries("Named"), "Named - Ch. 1.cbz", ["001.jpg", "002.jpg"], 1m);
        await IndexAsync();

        await Controller(_token).Push(Body(KoreaderDigest.FileName("Named - Ch. 1.cbz"), "2", 1), default);

        Assert.True(Progress(chapters[1m])!.Completed);
    }

    [Fact]
    public async Task A_name_two_files_share_matches_neither()
    {
        var (_, first) = SeedFile(SeedSeries("A"), "Ch. 1.cbz", ["001.jpg"], 1m);
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        var (_, second) = SeedFile(SeedSeries("B"), Path.Combine("b", "Ch. 1.cbz"), ["001.jpg", "002.jpg"], 1m);
        await IndexAsync();

        var result = (ObjectResult)await Controller(_token).Push(Body(KoreaderDigest.FileName("Ch. 1.cbz"), "1", 1), default);

        Assert.Equal(202, result.StatusCode);
        Assert.Null(Progress(first[1m]));
        Assert.Null(Progress(second[1m]));
    }

    [Fact]
    public async Task A_copy_downloaded_before_a_rewrite_still_resolves()
    {
        var (path, chapters) = SeedFile(SeedSeries("Rewritten"), "Rewritten.cbz", ["001.jpg", "002.jpg"], 1m);
        await IndexAsync();
        var before = KoreaderDigest.PartialMd5(path);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var stream = archive.CreateEntry("ComicInfo.xml").Open();
            stream.Write("<ComicInfo/>"u8);
        }

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await IndexAsync();
        Assert.NotEqual(before, KoreaderDigest.PartialMd5(path));

        await Controller(_token).Push(Body(before, "2", 1), default);

        Assert.True(Progress(chapters[1m])!.Completed);
    }

    [Fact]
    public async Task The_name_an_opds_download_was_served_under_resolves()
    {
        var (path, chapters) = SeedFile(SeedSeries("Served"), "Served.cbz", ["001.jpg", "002.jpg"], 1m);
        int fileId;
        await using (var db = _db.NewContext())
        {
            fileId = db.Chapters.Single(c => c.Id == chapters[1m]).ChapterFileId!.Value;
            await new KoreaderDocumentIndexer(db, NullLogger<KoreaderDocumentIndexer>.Instance)
                .RecordServedAsync(fileId, path, "Sérved - Ch. 1.cbz", default);
        }

        // ASP.NET's plain filename= parameter replaces what is not ASCII, and KOReader reads that one.
        await Controller(_token).Push(Body(KoreaderDigest.FileName("S_rved - Ch. 1.cbz"), "1", 0.5), default);

        Assert.Equal(0, Progress(chapters[1m])!.PageIndex);
    }

    [Fact]
    public async Task A_fully_incognito_series_records_progress_but_no_read_event()
    {
        var (path, chapters) = SeedVolume("Hidden", s => s.Incognito = IncognitoMode.Full);
        await IndexAsync();

        await Controller(_token).Push(Body(KoreaderDigest.PartialMd5(path), "6", 1), default);

        Assert.All(chapters.Values, id => Assert.True(Progress(id)!.Completed));
        Assert.Empty(ChaptersRead());
    }

    [Fact]
    public async Task Indexing_does_nothing_while_nobody_has_the_catalogue_on()
    {
        _db.SetUserConfig(_user, (SettingKeys.OpdsEnabled, "false"));
        SeedVolume();

        await IndexAsync();

        using var db = _db.NewContext();
        Assert.Empty(db.KoreaderDocuments);
    }

    [Theory]
    [InlineData(0, 2, false, 4, false, 1, true)]
    [InlineData(3, 3, false, 4, false, 1, null)]
    [InlineData(0, 6, true, 5, false, 5, true)]
    public void A_push_writes_chapters_behind_and_at_the_page(int start, int count, bool sharedWhole, int page,
        bool completed, int expectedPage, bool? expectedCompleted)
    {
        Assert.Equal(new KoreaderSyncService.Write(expectedPage, expectedCompleted),
            KoreaderSyncService.WriteFor(start, count, sharedWhole, page, 6, completed));
    }

    [Theory]
    [InlineData(2, 1, false, 4, true)]
    [InlineData(3, 3, false, 1, false)]
    [InlineData(0, 6, true, 4, false)]
    [InlineData(0, 6, true, 5, true)]
    public void A_push_leaves_finished_chapters_behind_and_chapters_ahead_alone(int start, int count,
        bool sharedWhole, int page, bool completed)
    {
        Assert.Null(KoreaderSyncService.WriteFor(start, count, sharedWhole, page, 6, completed));
    }

    [Fact]
    public void A_finished_chapter_points_at_the_next_chapters_first_page()
    {
        Assert.Equal(2, KoreaderSyncService.PageFor(0, 2, 6, 1, completed: true));
        Assert.Equal(5, KoreaderSyncService.PageFor(3, 3, 6, 2, completed: true));
        Assert.Equal(4, KoreaderSyncService.PageFor(3, 3, 6, 1, completed: false));
    }
}
