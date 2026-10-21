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
using Microsoft.Extensions.DependencyInjection;
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
    private readonly Clock _clock = new(DateTimeOffset.UtcNow);
    private readonly KoreaderPushWriter _writer;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public KoreaderSyncTests()
    {
        Directory.CreateDirectory(_root);
        _user = _db.SeedUser("koreader", MakiPermission.UseOpds, allRootFolders: true);
        _token = _db.SeedApiKey(_user, UserApiKeyScope.Opds);
        _db.SetUserConfig(_user, (SettingKeys.OpdsEnabled, "true"));
        _writer = Writer();
    }

    /// <summary>The push writer over its own scopes, as the host runs it.</summary>
    private KoreaderPushWriter Writer()
    {
        var services = new ServiceCollection();
        services.AddScoped<DataScope>();
        services.AddScoped(sp => new MakiDbContext(_db.Options, sp.GetRequiredService<DataScope>()));
        services.AddScoped(sp => Reader(sp.GetRequiredService<MakiDbContext>()));
        services.AddScoped(sp => new KoreaderSyncService(sp.GetRequiredService<MakiDbContext>(),
            sp.GetRequiredService<ReaderService>(), _cache));
        services.AddScoped(sp => new OpdsAccessService(sp.GetRequiredService<MakiDbContext>(), TimeProvider.System));
        services.AddScoped(sp => new KoreaderDocumentIndexer(sp.GetRequiredService<MakiDbContext>(),
            NullLogger<KoreaderDocumentIndexer>.Instance));
        return new KoreaderPushWriter(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new NoScheduler(), _clock, NullLogger<KoreaderPushWriter>.Instance);
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
            _writer,
            new KoreaderDocumentIndexer(db, NullLogger<KoreaderDocumentIndexer>.Instance),
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

    private async Task<int> IndexAsync()
    {
        await using var db = _db.NewContext();
        var added = await new KoreaderDocumentIndexer(db, NullLogger<KoreaderDocumentIndexer>.Instance)
            .RunAsync(CancellationToken.None);
        Assert.True(added >= 0);
        return added;
    }

    /// <summary>A push as KOReader makes it, then whatever the writer does with it.</summary>
    private async Task<IActionResult> Pushed(string? token, KoreaderProgressRequest body)
    {
        var result = await Controller(token).Push(body, default);
        await _writer.FlushAsync(default);
        return result;
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
        var result = await Pushed(_token, Body(KoreaderDigest.PartialMd5(path), "5", 0.8333));

        Assert.IsType<OkObjectResult>(result);
        Assert.True(Progress(chapters[1m])!.Completed);
        Assert.True(Progress(chapters[2m])!.Completed);
        var current = Progress(chapters[3m])!;
        Assert.False(current.Completed);
        Assert.Equal(1, current.PageIndex);
        Assert.Equal(2, ChaptersRead().Count);
    }

    [Fact]
    public async Task A_push_past_a_watched_chapter_turns_it_into_a_read()
    {
        var (path, chapters) = SeedVolume();
        await IndexAsync();
        await using (var db = _db.NewContext(_user))
        {
            await Reader(db).MarkWatchedAsync([chapters[1m], chapters[2m]], default);
        }

        await Pushed(_token, Body(KoreaderDigest.PartialMd5(path), "5", 0.8333));

        Assert.All([chapters[1m], chapters[2m]], id =>
        {
            var row = Progress(id)!;
            Assert.True(row.Completed);
            Assert.False(row.Watched);
        });
    }

    [Fact]
    public async Task The_last_page_completes_a_single_chapter_file()
    {
        var (path, chapters) = SeedFile(SeedSeries("Solo"), "Solo.cbz", ["001.jpg", "002.jpg", "003.jpg"], 7m);
        await IndexAsync();

        await Pushed(_token, Body(KoreaderDigest.PartialMd5(path), "3", 1));

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
    public async Task An_unknown_document_answers_200_and_writes_nothing()
    {
        var (_, chapters) = SeedVolume();
        await IndexAsync();

        var result = await Pushed(_token, Body(new string('a', 32), "5", 0.8));

        // Anything but 200 is a failure to KOReader, which queues it and stops draining at it.
        Assert.IsType<OkObjectResult>(result);
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

        var result = await Pushed(_token, Body(KoreaderDigest.PartialMd5(path), "6", 1));

        Assert.IsType<OkObjectResult>(result);
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
        await Pushed(_token, Body(digest, "4", 0.6667));

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

        await Pushed(_token, Body(KoreaderDigest.FileName("Named - Ch. 1.cbz"), "2", 1));

        Assert.True(Progress(chapters[1m])!.Completed);
    }

    [Fact]
    public async Task A_name_two_files_share_matches_neither()
    {
        var (_, first) = SeedFile(SeedSeries("A"), "Ch. 1.cbz", ["001.jpg"], 1m);
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        var (_, second) = SeedFile(SeedSeries("B"), Path.Combine("b", "Ch. 1.cbz"), ["001.jpg", "002.jpg"], 1m);
        await IndexAsync();

        var result = await Pushed(_token, Body(KoreaderDigest.FileName("Ch. 1.cbz"), "1", 1));

        Assert.IsType<OkObjectResult>(result);
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

        await Pushed(_token, Body(before, "2", 1));

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
        await Pushed(_token, Body(KoreaderDigest.FileName("S_rved - Ch. 1.cbz"), "1", 0.5));

        Assert.Equal(0, Progress(chapters[1m])!.PageIndex);
    }

    [Fact]
    public async Task A_push_for_a_file_the_index_has_not_reached_lands_after_the_next_pass()
    {
        var (path, chapters) = SeedVolume();

        await Pushed(_token, Body(KoreaderDigest.PartialMd5(path), "5", 0.8333));
        Assert.All(chapters.Values, id => Assert.Null(Progress(id)));
        Assert.Equal(1, _writer.WaitingCount);

        Assert.True(await IndexAsync() > 0);
        _writer.RetryWaiting();
        await _writer.FlushAsync(default);

        Assert.True(Progress(chapters[2m])!.Completed);
        Assert.Equal(1, Progress(chapters[3m])!.PageIndex);
        Assert.Equal(0, _writer.WaitingCount);
    }

    [Fact]
    public async Task A_later_push_for_the_same_document_replaces_the_one_waiting()
    {
        var (path, chapters) = SeedVolume();
        var digest = KoreaderDigest.PartialMd5(path);

        await Pushed(_token, Body(digest, "1", 0.1667));
        await Pushed(_token, Body(digest, "4", 0.6667));
        Assert.Equal(1, _writer.WaitingCount);

        await IndexAsync();
        _writer.RetryWaiting();
        await _writer.FlushAsync(default);

        Assert.True(Progress(chapters[2m])!.Completed);
    }

    [Fact]
    public async Task A_waiting_push_is_dropped_once_its_owner_cannot_use_opds()
    {
        var (path, chapters) = SeedVolume();
        await Pushed(_token, Body(KoreaderDigest.PartialMd5(path), "6", 1));
        using (var db = _db.NewContext())
        {
            db.Users.Single(u => u.Id == _user).Permissions = default;
            db.SaveChanges();
        }

        await IndexAsync();
        _writer.RetryWaiting();
        await _writer.FlushAsync(default);

        Assert.All(chapters.Values, id => Assert.Null(Progress(id)));
    }

    [Fact]
    public async Task A_waiting_push_is_dropped_after_a_week()
    {
        var (path, chapters) = SeedVolume();
        await Pushed(_token, Body(KoreaderDigest.PartialMd5(path), "6", 1));

        _clock.Now += KoreaderPushWriter.WaitFor + TimeSpan.FromMinutes(1);
        await IndexAsync();
        _writer.RetryWaiting();
        await _writer.FlushAsync(default);

        Assert.All(chapters.Values, id => Assert.Null(Progress(id)));
        Assert.Equal(0, _writer.WaitingCount);
    }

    [Fact]
    public async Task Only_a_file_no_pass_has_seen_asks_for_an_early_pass()
    {
        await IndexAsync();
        await using var db = _db.NewContext();
        var indexer = new KoreaderDocumentIndexer(db, NullLogger<KoreaderDocumentIndexer>.Instance);
        Assert.False(await indexer.AnyUnindexedAsync(default));

        SeedFile(SeedSeries("New"), "New.cbz", ["001.jpg"], 1m);
        Assert.True(await indexer.AnyUnindexedAsync(default));
        await IndexAsync();
        Assert.False(await indexer.AnyUnindexedAsync(default));

        // A file the pass cannot read gets no ids, and must not set a pass off on every later miss.
        var seriesId = SeedSeries("Gone");
        db.ChapterFiles.Add(new ChapterFile
        {
            SeriesId = seriesId, RelativePath = "Gone.cbz", Size = 1, SourceName = "test", DateAdded = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        Assert.True(await indexer.AnyUnindexedAsync(default));
        await IndexAsync();
        Assert.False(await indexer.AnyUnindexedAsync(default));
    }

    [Fact]
    public async Task A_fully_incognito_series_records_progress_but_no_read_event()
    {
        var (path, chapters) = SeedVolume("Hidden", s => s.Incognito = IncognitoMode.Full);
        await IndexAsync();

        await Pushed(_token, Body(KoreaderDigest.PartialMd5(path), "6", 1));

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
