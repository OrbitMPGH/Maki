using System.Data.Common;
using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Api.Configuration;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Notifications;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Core.Storage;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="SeriesArchiveService"/>: what archive keeps and cancels, what it refuses, what removing
/// the files stamps, and what restore brings back. The service is built the way the DI container
/// builds it, over a temp root folder, with a <see cref="FakeSource"/> behind the refresh restore runs.
/// </summary>
public sealed class SeriesArchiveTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly RecordingNotifications _notifications = new();
    private readonly DownloadBatchNotifier _batches = new(
        new RecordingNotifications(), new RecordingInbox(), new TestLocalizer(), new TestUserLocaleResolver(),
        TimeProvider.System, NullLogger<DownloadBatchNotifier>.Instance);
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "maki-archive-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>What the fake source lists for every series; restore's refresh reads it.</summary>
    private Func<IReadOnlyList<SourceChapter>> _listing = () => [];

    public SeriesArchiveTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        _batches.Dispose();
        _db.Dispose();
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static SourceMapping Mapping() => new()
    {
        SourceName = "fake", SourceSeriesId = "s", Url = "https://fake.test", Priority = 1, Enabled = true,
    };

    private int SeedSeries(string title = "Berserk", NewChapterMonitorMode monitor = NewChapterMonitorMode.All,
        Action<Series>? configure = null)
    {
        var id = _db.SeedSeries(title, monitor, configure: configure, mappings: [Mapping()]);
        using var db = _db.NewContext();
        db.RootFolders.Single(r => r.Id == db.Series.Single(s => s.Id == id).RootFolderId).Path = _tmp;
        db.SaveChanges();
        return id;
    }

    /// <summary>A chapter, with a real archive on disk when <paramref name="withFile"/>.</summary>
    private (int ChapterId, string? Path) SeedChapter(
        int seriesId, decimal number, bool withFile = true, bool wanted = true, DateTime? removedAt = null)
    {
        using var db = _db.NewContext();
        var series = db.Series.Single(s => s.Id == seriesId);
        int? fileId = null;
        string? path = null;
        if (withFile)
        {
            var relative = Path.Combine(series.FolderName, $"ch{number}.cbz");
            path = Path.Combine(_tmp, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var page = zip.CreateEntry("001.jpg").Open();
                page.WriteByte(0xFF);
            }

            var file = new ChapterFile
            {
                SeriesId = seriesId, RelativePath = relative, Size = new FileInfo(path).Length, SourceName = "fake",
                DateAdded = DateTime.UtcNow,
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            fileId = file.Id;
        }

        var chapter = new Chapter
        {
            SeriesId = seriesId, Number = number, Language = "en", Wanted = wanted, ChapterFileId = fileId,
            FileRemovedAt = removedAt,
        };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return (chapter.Id, path);
    }

    private int SeedQueueRow(int seriesId, int? chapterId, QueueStatus status,
        AcquisitionProtocol protocol = AcquisitionProtocol.Scraper, int? healthOperationId = null)
    {
        using var db = _db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = seriesId, ChapterId = chapterId, Protocol = protocol, Status = status,
            QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.Manual, HealthOperationId = healthOperationId,
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private SeriesArchiveService Service(MakiDbContext db, RecycleBinMover? mover = null)
    {
        var fake = new FakeSource { Name = "fake", OnListChapters = _ => _listing() };
        var registry = new SourceRegistry([fake]);
        var queue = new DownloadQueueService(
            _db.ScopeFactory(), TimeProvider.System, Sources.Resolver(registry), NullLogger<DownloadQueueService>.Instance);
        var archives = new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance);
        var paths = new AppPaths(Path.Combine(_tmp, "config"));
        var localizer = new TestLocalizer();

        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddSingleton(registry);
        services.AddSingleton(Sources.AllEnabled);
        services.AddSingleton(new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance));
        services.AddSingleton<IAppSettings>(new FakeAppSettings());
        services.AddSingleton(queue);
        services.AddSingleton<NotificationService>(_notifications);
        services.AddSingleton<InboxService>(new RecordingInbox());
        services.AddSingleton(_batches);
        services.AddSingleton<Maki.Api.Localization.IMessageCatalog>(localizer);
        services.AddSingleton<Maki.Api.Localization.IUserLocaleResolver>(new TestUserLocaleResolver());
        services.AddLogging();
        services.AddScoped<ChapterSyncService>();

        return new SeriesArchiveService(
            db, TestDeletion.For(db, mover is null ? null : TestRecycleBin.For(db, mover: mover)), queue, _batches, archives,
            new CoverService(null!, paths, new FakeAppSettings(), NullLogger<CoverService>.Instance),
            _notifications, localizer, new TestUserLocaleResolver(), new TestCurrentUser(1), paths, TimeProvider.System,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SeriesArchiveService>.Instance);
    }

    private async Task<SeriesArchiveResult> ArchiveAsync(int seriesId, bool removeFiles = false, bool permanent = false)
    {
        using var db = _db.NewContext();
        return await Service(db).ArchiveAsync(seriesId, removeFiles, permanent, CancellationToken.None);
    }

    private async Task<SeriesArchiveResult> RestoreAsync(int seriesId, bool redownloadRemoved = false)
    {
        using var db = _db.NewContext();
        var result = await Service(db).RestoreAsync(seriesId, redownloadRemoved, CancellationToken.None);
        if (result.Refresh is { } refresh)
        {
            await refresh;
        }

        return result;
    }

    private Series Load(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Series.AsNoTracking().Single(s => s.Id == seriesId);
    }

    // ---- dialog figures ----

    [Fact]
    public async Task FreeableBytes_leaves_out_hardlinked_files()
    {
        var seriesId = SeedSeries();
        var (_, kept) = SeedChapter(seriesId, 1m);
        var (_, seeded) = SeedChapter(seriesId, 2m);
        Assert.True(FileLinker.TryHardlink(seeded!, Path.Combine(_tmp, "seeding.cbz")), "this filesystem cannot hardlink");
        using var db = _db.NewContext();
        var series = await db.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId);

        var freeable = await SeriesArchiveService.FreeableBytesAsync(db, series, CancellationToken.None);

        Assert.Equal(new FileInfo(kept!).Length, freeable);
    }

    private void SeedProgress(int seriesId, int chapterId, int userId, DateTime updated, bool completed = false)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId, SeriesId = seriesId, ChapterId = chapterId, PageIndex = completed ? 19 : 1, PageCount = 20,
            StartedAt = updated, UpdatedAt = updated, Completed = completed,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task OtherActiveReaders_counts_other_users_with_recent_unfinished_progress()
    {
        var seriesId = SeedSeries();
        var (chapterId, _) = SeedChapter(seriesId, 1m);
        var now = DateTime.UtcNow;
        var recent = _db.SeedUser("recent");
        var stale = _db.SeedUser("stale");
        var finished = _db.SeedUser("finished");
        SeedProgress(seriesId, chapterId, 1, now.AddDays(-1));
        SeedProgress(seriesId, chapterId, recent, now.AddDays(-2));
        SeedProgress(seriesId, chapterId, stale, now.AddDays(-45));
        SeedProgress(seriesId, chapterId, finished, now.AddDays(-1), completed: true);
        using (var db = _db.NewContext())
        {
            db.ReadingStates.Add(new ReadingState
            {
                UserId = finished, SeriesId = seriesId, Title = "Berserk", MaxChapter = 1, Finished = true,
                LastProgressAt = now, UpdatedAt = now,
            });
            db.SaveChanges();
        }

        using var read = _db.NewContext(1);
        var others = await SeriesArchiveService.OtherActiveReadersAsync(read, seriesId, 1, now, CancellationToken.None);

        Assert.Equal(1, others);
    }

    [Fact]
    public async Task OtherActiveReaders_counts_a_reader_between_chapters()
    {
        var seriesId = SeedSeries();
        var (first, _) = SeedChapter(seriesId, 1m);
        SeedChapter(seriesId, 2m);
        var now = DateTime.UtcNow;
        var between = _db.SeedUser("between");
        SeedProgress(seriesId, first, between, now.AddDays(-3), completed: true);

        using var read = _db.NewContext(1);
        var others = await SeriesArchiveService.OtherActiveReadersAsync(read, seriesId, 1, now, CancellationToken.None);

        Assert.Equal(1, others);
    }

    // ---- archive ----

    [Fact]
    public async Task Archive_keeps_every_row_the_series_owns()
    {
        var seriesId = SeedSeries();
        var (first, firstPath) = SeedChapter(seriesId, 1m);
        SeedChapter(seriesId, 2m);
        SeedChapter(seriesId, 3m, withFile: false);
        using (var db = _db.NewContext())
        {
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1, SeriesId = seriesId, ChapterId = first, PageIndex = 3, PageCount = 20,
                StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.ReaderBookmarks.Add(new ReaderBookmark
            {
                UserId = 1, SeriesId = seriesId, ChapterId = first, PageIndex = 2, CreatedAt = DateTime.UtcNow,
            });
            db.UserSeriesStates.Add(new UserSeriesState { UserId = 1, SeriesId = seriesId, Rating = 8, UpdatedAt = DateTime.UtcNow });
            db.SeriesScrobbleStates.Add(new SeriesScrobbleState
            {
                UserId = 1, SeriesId = seriesId, Service = "anilist", Chapter = 3, SyncedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        (int, int, int, int, int, int) Counts()
        {
            using var db = _db.NewContext();
            return (
                db.Chapters.IgnoreQueryFilters().Count(c => c.SeriesId == seriesId),
                db.ChapterProgress.IgnoreQueryFilters().Count(c => c.SeriesId == seriesId),
                db.ReaderBookmarks.IgnoreQueryFilters().Count(c => c.SeriesId == seriesId),
                db.UserSeriesStates.IgnoreQueryFilters().Count(c => c.SeriesId == seriesId),
                db.SeriesScrobbleStates.IgnoreQueryFilters().Count(c => c.SeriesId == seriesId),
                db.ChapterFiles.IgnoreQueryFilters().Count(c => c.SeriesId == seriesId));
        }

        var before = Counts();
        var result = await ArchiveAsync(seriesId);

        Assert.True(result.Succeeded);
        Assert.Equal((3, 1, 1, 1, 1, 2), before);
        Assert.Equal(before, Counts());
        Assert.True(File.Exists(firstPath));
    }

    [Fact]
    public async Task Archive_stamps_the_series_with_the_time_and_the_user()
    {
        var seriesId = SeedSeries();

        await ArchiveAsync(seriesId);

        var series = Load(seriesId);
        Assert.NotNull(series.ArchivedAt);
        Assert.Equal(1, series.ArchivedByUserId);
    }

    [Fact]
    public async Task Archive_dispatches_a_series_archived_notification()
    {
        var seriesId = SeedSeries("Berserk");

        await ArchiveAsync(seriesId);

        var (type, message) = Assert.Single(_notifications.Sent);
        Assert.Equal(NotificationEventType.SeriesArchived, type);
        Assert.Equal(seriesId, message.SeriesId);
        Assert.Equal("Berserk", message.SeriesTitle);
    }

    [Fact]
    public async Task Archive_cancels_the_series_not_started_queue_rows_and_leaves_other_series_alone()
    {
        var seriesId = SeedSeries("Berserk");
        var otherId = SeedSeries("Vagabond");
        foreach (var (number, status) in new[] { (1m, QueueStatus.Queued), (2m, QueueStatus.Failed), (3m, QueueStatus.RateLimited) })
        {
            SeedQueueRow(seriesId, SeedChapter(seriesId, number, withFile: false).ChapterId, status);
        }

        SeedQueueRow(otherId, SeedChapter(otherId, 1m, withFile: false).ChapterId, QueueStatus.Queued);

        var result = await ArchiveAsync(seriesId);

        Assert.True(result.Succeeded);
        using var db = _db.NewContext();
        Assert.Empty(db.DownloadQueue.Where(q => q.SeriesId == seriesId));
        Assert.Equal(QueueStatus.Queued, Assert.Single(db.DownloadQueue.Where(q => q.SeriesId == otherId)).Status);
    }

    [Fact]
    public async Task Archive_dismisses_pending_proposals_and_leaves_other_series_alone()
    {
        var seriesId = SeedSeries("Berserk");
        var otherId = SeedSeries("Vagabond");
        using (var db = _db.NewContext())
        {
            foreach (var id in new[] { seriesId, otherId })
            {
                db.TorrentProposals.Add(new TorrentProposal
                {
                    SeriesId = id, ReleaseGuid = $"g{id}", Title = "Berserk v01", Status = TorrentProposalStatus.Pending,
                    CreatedAtUtc = DateTime.UtcNow,
                });
            }

            db.SaveChanges();
        }

        await ArchiveAsync(seriesId);

        using var check = _db.NewContext();
        var archived = check.TorrentProposals.Single(p => p.SeriesId == seriesId);
        Assert.Equal(TorrentProposalStatus.Dismissed, archived.Status);
        Assert.Equal(1, archived.ResolvedByUserId);
        Assert.NotNull(archived.ResolvedAtUtc);
        Assert.Equal(TorrentProposalStatus.Pending, check.TorrentProposals.Single(p => p.SeriesId == otherId).Status);
    }

    [Fact]
    public async Task Archive_refuses_while_a_chapter_is_downloading()
    {
        var seriesId = SeedSeries();
        SeedQueueRow(seriesId, SeedChapter(seriesId, 1m, withFile: false).ChapterId, QueueStatus.Downloading);
        SeedQueueRow(seriesId, SeedChapter(seriesId, 2m, withFile: false).ChapterId, QueueStatus.Queued);

        var result = await ArchiveAsync(seriesId);

        Assert.Equal(SeriesArchiveStatus.Conflict, result.Status);
        Assert.Equal("error.series.activeDownloadArchive", result.ErrorKey);
        Assert.Null(Load(seriesId).ArchivedAt);
        using var db = _db.NewContext();
        Assert.Equal(2, db.DownloadQueue.Count(q => q.SeriesId == seriesId));
    }

    [Theory]
    [InlineData(true, PendingImportLink.None)]
    [InlineData(false, PendingImportLink.Link)]
    [InlineData(false, PendingImportLink.LinkAndComicInfo)]
    public async Task Archive_refuses_while_a_source_match_or_import_link_is_pending(bool matchPending, PendingImportLink link)
    {
        var seriesId = SeedSeries(configure: s =>
        {
            s.SourceMatchPending = matchPending;
            s.PendingImportLink = link;
        });

        var result = await ArchiveAsync(seriesId);

        Assert.Equal("error.series.activeDownloadArchive", result.ErrorKey);
        Assert.Null(Load(seriesId).ArchivedAt);
    }

    [Fact]
    public async Task Archive_of_an_archived_series_answers_archived()
    {
        var seriesId = SeedSeries();
        await ArchiveAsync(seriesId);

        var result = await ArchiveAsync(seriesId);

        Assert.Equal(SeriesArchiveStatus.Conflict, result.Status);
        Assert.Equal("error.series.archived", result.ErrorKey);
    }

    [Fact]
    public async Task Archive_of_a_missing_series_is_not_found()
    {
        Assert.Equal(SeriesArchiveStatus.NotFound, (await ArchiveAsync(9999)).Status);
    }

    [Fact]
    public async Task Archive_with_removeFiles_stamps_removal_keeps_wanted_and_bins_the_files()
    {
        var seriesId = SeedSeries();
        var (wantedChapter, wantedPath) = SeedChapter(seriesId, 1m, wanted: true);
        var (unwantedChapter, unwantedPath) = SeedChapter(seriesId, 2m, wanted: false);
        var (fileless, _) = SeedChapter(seriesId, 3m, withFile: false);

        var result = await ArchiveAsync(seriesId, removeFiles: true);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.FilesDeleted);
        Assert.False(File.Exists(wantedPath));
        Assert.False(File.Exists(unwantedPath));
        using var db = _db.NewContext();
        var chapters = db.Chapters.AsNoTracking().Where(c => c.SeriesId == seriesId).ToDictionary(c => c.Id);
        Assert.NotNull(chapters[wantedChapter].FileRemovedAt);
        Assert.NotNull(chapters[unwantedChapter].FileRemovedAt);
        Assert.Null(chapters[fileless].FileRemovedAt);
        Assert.True(chapters[wantedChapter].Wanted);
        Assert.False(chapters[unwantedChapter].Wanted);
        Assert.True(chapters[fileless].Wanted);
        Assert.All(chapters.Values, c => Assert.Null(c.ChapterFileId));
        var bin = db.RecycleBin.AsNoTracking().Where(b => b.SeriesId == seriesId).ToList();
        Assert.Equal(2, bin.Count);
        Assert.All(bin, b =>
        {
            Assert.Equal(RecycleReason.SeriesArchive, b.Reason);
            Assert.True(File.Exists(Path.Combine(_tmp, b.BinPath)));
        });
    }

    [Fact]
    public async Task Archive_with_removeFiles_and_permanent_deletes_without_the_bin()
    {
        var seriesId = SeedSeries();
        var (chapterId, path) = SeedChapter(seriesId, 1m);

        var result = await ArchiveAsync(seriesId, removeFiles: true, permanent: true);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(path));
        using var db = _db.NewContext();
        Assert.Empty(db.RecycleBin);
        Assert.NotNull(db.Chapters.Single(c => c.Id == chapterId).FileRemovedAt);
    }

    [Fact]
    public async Task RemoveFiles_clears_the_files_of_a_series_archived_with_them_kept()
    {
        var seriesId = SeedSeries();
        var (chapterId, path) = SeedChapter(seriesId, 1m);
        await ArchiveAsync(seriesId);
        Assert.True(File.Exists(path));

        using (var db = _db.NewContext())
        {
            var result = await Service(db).RemoveFilesAsync(seriesId, permanent: false, CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal(1, result.FilesDeleted);
        }

        Assert.False(File.Exists(path));
        using var check = _db.NewContext();
        Assert.NotNull(check.Chapters.Single(c => c.Id == chapterId).FileRemovedAt);
        Assert.Equal(RecycleReason.SeriesArchive, Assert.Single(check.RecycleBin).Reason);
    }

    [Fact]
    public async Task RemoveFiles_on_an_active_series_answers_not_archived()
    {
        var seriesId = SeedSeries();
        var (_, path) = SeedChapter(seriesId, 1m);

        using var db = _db.NewContext();
        var result = await Service(db).RemoveFilesAsync(seriesId, permanent: false, CancellationToken.None);

        Assert.Equal(SeriesArchiveStatus.Conflict, result.Status);
        Assert.Equal("error.series.notArchived", result.ErrorKey);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ArchiveMany_refuses_a_busy_series_without_stopping_the_rest()
    {
        var busy = SeedSeries("Busy");
        var idle = SeedSeries("Idle");
        SeedQueueRow(busy, SeedChapter(busy, 1m, withFile: false).ChapterId, QueueStatus.Importing);

        using var db = _db.NewContext();
        var results = await Service(db).ArchiveManyAsync([busy, idle, busy], false, false, CancellationToken.None);

        Assert.Equal([busy, idle], results.Select(r => r.SeriesId));
        Assert.Equal("error.series.activeDownloadArchive", results[0].Result.ErrorKey);
        Assert.True(results[1].Result.Succeeded);
        Assert.Null(Load(busy).ArchivedAt);
        Assert.NotNull(Load(idle).ArchivedAt);
    }

    [Fact]
    public async Task Archive_refused_by_the_bin_leaves_the_series_in_the_library_and_its_queue_untouched()
    {
        var seriesId = SeedSeries();
        var (_, path) = SeedChapter(seriesId, 1m);
        SeedQueueRow(seriesId, SeedChapter(seriesId, 2m, withFile: false).ChapterId, QueueStatus.Queued);
        var crossVolume = new ScriptedMover(() => new MoveOutcome(MoveResult.CrossVolume));

        SeriesArchiveResult result;
        using (var db = _db.NewContext())
        {
            result = await Service(db, crossVolume).ArchiveAsync(seriesId, removeFiles: true, permanent: false, CancellationToken.None);
        }

        Assert.Equal(SeriesArchiveStatus.BinRefused, result.Status);
        Assert.Equal("error.recycleBin.crossVolume", result.ErrorKey);
        Assert.Null(Load(seriesId).ArchivedAt);
        Assert.True(File.Exists(path));
        using var check = _db.NewContext();
        Assert.Equal(QueueStatus.Queued, Assert.Single(check.DownloadQueue.Where(q => q.SeriesId == seriesId)).Status);
    }

    [Fact]
    public async Task Archive_with_removeFiles_prunes_the_series_folder()
    {
        var seriesId = SeedSeries("Berserk");
        var (_, path) = SeedChapter(seriesId, 1m);
        var folder = Path.GetDirectoryName(path)!;

        Assert.True((await ArchiveAsync(seriesId, removeFiles: true)).Succeeded);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Archive_with_removeFiles_leaves_a_folder_another_series_shares()
    {
        var seriesId = SeedSeries("Berserk");
        var (_, path) = SeedChapter(seriesId, 1m);
        var rootFolderId = Load(seriesId).RootFolderId;
        var otherId = SeedSeries("Berserk Deluxe", configure: s =>
        {
            s.RootFolderId = rootFolderId;
            s.FolderName = "Berserk";
        });
        var (_, otherPath) = SeedChapter(otherId, 2m);

        Assert.True((await ArchiveAsync(seriesId, removeFiles: true)).Succeeded);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(otherPath));
    }

    [Fact]
    public async Task Archive_cancels_torrent_rows_the_client_still_holds_instead_of_deleting_them()
    {
        var seriesId = SeedSeries();
        var downloading = SeedQueueRow(seriesId, null, QueueStatus.Downloading, AcquisitionProtocol.Torrent);
        var awaiting = SeedQueueRow(seriesId, null, QueueStatus.AwaitingImport, AcquisitionProtocol.Torrent);

        Assert.True((await ArchiveAsync(seriesId)).Succeeded);

        using var db = _db.NewContext();
        var rows = db.DownloadQueue.Where(q => q.SeriesId == seriesId).ToDictionary(q => q.Id, q => q.Status);
        Assert.Equal(QueueStatus.Cancelled, rows[downloading]);
        Assert.Equal(QueueStatus.Cancelled, rows[awaiting]);
    }

    [Fact]
    public async Task Archive_leaves_a_queued_health_repair_to_finish_and_restore_then_queues_normally()
    {
        var seriesId = SeedSeries();
        var (repaired, path) = SeedChapter(seriesId, 1m);
        var (removed, _) = SeedChapter(seriesId, 2m, withFile: false, removedAt: DateTime.UtcNow);
        var plain = SeedQueueRow(seriesId, SeedChapter(seriesId, 3m, withFile: false).ChapterId, QueueStatus.Queued);
        int operationId;
        using (var db = _db.NewContext())
        {
            var file = new HealthFile
            {
                RootFolderId = db.Series.Single(s => s.Id == seriesId).RootFolderId, SeriesId = seriesId,
                RelativePath = Path.GetRelativePath(_tmp, path!),
            };
            db.HealthFiles.Add(file);
            db.SaveChanges();
            var operation = new HealthOperation
            {
                Kind = "repair", Status = "downloading", FileId = file.Id, Version = file.Version, UserId = 1,
            };
            db.HealthOperations.Add(operation);
            db.SaveChanges();
            operationId = operation.Id;
        }

        var repair = SeedQueueRow(seriesId, repaired, QueueStatus.Queued, healthOperationId: operationId);

        Assert.True((await ArchiveAsync(seriesId)).Succeeded);

        using (var db = _db.NewContext())
        {
            Assert.Equal(QueueStatus.Queued, db.DownloadQueue.Single(q => q.Id == repair).Status);
            Assert.False(db.DownloadQueue.Any(q => q.Id == plain));

            // What the health worker does once the repair's row settles; with the row deleted it never could.
            db.DownloadQueue.Single(q => q.Id == repair).Status = QueueStatus.Completed;
            db.HealthOperations.Single(o => o.Id == operationId).Status = "completed";
            db.SaveChanges();
        }

        var restored = await RestoreAsync(seriesId, redownloadRemoved: true);

        Assert.Null(restored.QueueError);
        Assert.Equal(1, restored.Queued);
        using var check = _db.NewContext();
        Assert.True(check.DownloadQueue.Any(q => q.ChapterId == removed && q.Status != QueueStatus.Completed));
    }

    [Fact]
    public async Task Archive_finishes_once_the_queue_sweep_started_even_when_the_request_drops()
    {
        var seriesId = SeedSeries();
        SeedQueueRow(seriesId, SeedChapter(seriesId, 1m, withFile: false).ChapterId, QueueStatus.Queued);
        using var dropped = new CancellationTokenSource();
        var options = new DbContextOptionsBuilder<MakiDbContext>(_db.Options)
            .AddInterceptors(new CancelOnQuery("TorrentProposals", dropped))
            .Options;

        SeriesArchiveResult result;
        using (var db = new MakiDbContext(options))
        {
            result = await Service(db).ArchiveAsync(seriesId, removeFiles: false, permanent: false, dropped.Token);
        }

        Assert.True(dropped.IsCancellationRequested);
        Assert.True(result.Succeeded);
        Assert.NotNull(Load(seriesId).ArchivedAt);
        using var check = _db.NewContext();
        Assert.Empty(check.DownloadQueue.Where(q => q.SeriesId == seriesId));
    }

    [Fact]
    public async Task The_recycle_bin_lists_files_removed_by_an_archive_as_seriesArchive()
    {
        var seriesId = SeedSeries();
        SeedChapter(seriesId, 1m);
        await ArchiveAsync(seriesId, removeFiles: true);

        using var db = _db.NewContext();
        var list = Assert.IsType<OkObjectResult>(
            await new RecycleBinController(db, TestRecycleBin.For(db), new FakeAppSettings(), new TestLocalizer()).List(default));

        Assert.Equal("seriesArchive", Assert.Single(Assert.IsType<RecycleBinController.BinDto>(list.Value).Entries).Reason);
    }

    // ---- restore ----

    [Fact]
    public async Task Restore_clears_both_columns_and_the_check_time_and_keeps_the_monitoring_mode()
    {
        var seriesId = SeedSeries(monitor: NewChapterMonitorMode.MainOnly,
            configure: s => s.NextCheckAt = DateTime.UtcNow.AddHours(5));
        await ArchiveAsync(seriesId);
        Assert.Equal(1, Load(seriesId).ArchivedByUserId);

        // The refresh restore starts writes NextCheckAt itself, so it is held until the state is read.
        using var gate = new ManualResetEventSlim();
        _listing = () =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return [];
        };

        using var db = _db.NewContext();
        var result = await Service(db).RestoreAsync(seriesId, redownloadRemoved: false, CancellationToken.None);
        var series = Load(seriesId);
        gate.Set();
        await result.Refresh!;

        Assert.True(result.Succeeded);
        Assert.Null(series.ArchivedAt);
        Assert.Null(series.ArchivedByUserId);
        Assert.Null(series.NextCheckAt);
        Assert.Equal(NewChapterMonitorMode.MainOnly, series.MonitorNewItems);
        Assert.Equal(NewChapterMonitorMode.MainOnly, Load(seriesId).MonitorNewItems);
    }

    [Fact]
    public async Task Restore_marks_the_series_so_the_first_sync_stamps_no_first_seen()
    {
        var seriesId = SeedSeries();
        await ArchiveAsync(seriesId);
        Assert.Null(Load(seriesId).RestoredAt);

        using var gate = new ManualResetEventSlim();
        _listing = () =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return [];
        };

        using var db = _db.NewContext();
        var result = await Service(db).RestoreAsync(seriesId, redownloadRemoved: false, CancellationToken.None);
        var restoredAt = Load(seriesId).RestoredAt;
        gate.Set();
        await result.Refresh!;

        Assert.NotNull(restoredAt);
    }

    [Fact]
    public async Task Restore_of_an_active_series_answers_not_archived()
    {
        var seriesId = SeedSeries();

        var result = await RestoreAsync(seriesId);

        Assert.Equal(SeriesArchiveStatus.Conflict, result.Status);
        Assert.Equal("error.series.notArchived", result.ErrorKey);
        Assert.Null(result.Refresh);
    }

    [Fact]
    public async Task Restore_of_a_missing_series_is_not_found()
    {
        Assert.Equal(SeriesArchiveStatus.NotFound, (await RestoreAsync(9999)).Status);
    }

    [Fact]
    public async Task Restore_refreshes_the_series_so_a_chapter_released_meanwhile_is_queued()
    {
        var seriesId = SeedSeries();
        SeedChapter(seriesId, 1m);
        await ArchiveAsync(seriesId);
        var fake = new FakeSource { Name = "fake" };
        _listing = () => [fake.Chapter(1), fake.Chapter(2)];

        await RestoreAsync(seriesId);

        using var db = _db.NewContext();
        var chapters = db.Chapters.Where(c => c.SeriesId == seriesId).OrderBy(c => c.Number).ToList();
        Assert.Equal([1m, 2m], chapters.Select(c => c.Number!.Value));
        var queued = Assert.Single(db.DownloadQueue.Where(q => q.SeriesId == seriesId));
        Assert.Equal(chapters[1].Id, queued.ChapterId);
        Assert.Equal(DownloadOrigin.MonitorRefresh, queued.Origin);
    }

    [Fact]
    public async Task Restore_does_not_refresh_before_the_series_is_back()
    {
        var seriesId = SeedSeries();
        await ArchiveAsync(seriesId);
        var fake = new FakeSource { Name = "fake" };
        ArchivedAtSeenByTheRefresh = null;
        _listing = () =>
        {
            ArchivedAtSeenByTheRefresh = Load(seriesId).ArchivedAt;
            return [fake.Chapter(1)];
        };

        await RestoreAsync(seriesId);

        Assert.Null(ArchivedAtSeenByTheRefresh);
        using var db = _db.NewContext();
        Assert.Single(db.Chapters.Where(c => c.SeriesId == seriesId));
    }

    private DateTime? ArchivedAtSeenByTheRefresh { get; set; }

    [Fact]
    public async Task Restore_with_redownloadRemoved_queues_exactly_the_wanted_removed_chapters()
    {
        var seriesId = SeedSeries();
        var removedAt = DateTime.UtcNow.AddDays(-3);
        var (wantedA, _) = SeedChapter(seriesId, 1m, withFile: false, wanted: true, removedAt: removedAt);
        var (wantedB, _) = SeedChapter(seriesId, 2m, withFile: false, wanted: true, removedAt: removedAt);
        var (unwanted, _) = SeedChapter(seriesId, 3m, withFile: false, wanted: false, removedAt: removedAt);
        var (neverDownloaded, _) = SeedChapter(seriesId, 4m, withFile: false, wanted: true);
        await ArchiveAsync(seriesId);
        var fake = new FakeSource { Name = "fake" };
        _listing = () => [fake.Chapter(1), fake.Chapter(2), fake.Chapter(3), fake.Chapter(4)];

        var result = await RestoreAsync(seriesId, redownloadRemoved: true);

        Assert.Equal(2, result.Queued);
        Assert.Null(result.QueueError);
        using var db = _db.NewContext();
        var chapters = db.Chapters.AsNoTracking().Where(c => c.SeriesId == seriesId).ToDictionary(c => c.Id);
        Assert.Null(chapters[wantedA].FileRemovedAt);
        Assert.Null(chapters[wantedB].FileRemovedAt);
        Assert.NotNull(chapters[unwanted].FileRemovedAt);
        Assert.Null(chapters[neverDownloaded].FileRemovedAt);
        Assert.Equal(
            new HashSet<int> { wantedA, wantedB },
            db.DownloadQueue.Where(q => q.SeriesId == seriesId).Select(q => q.ChapterId!.Value).ToHashSet());
    }

    [Fact]
    public async Task Restore_without_redownloadRemoved_leaves_removed_chapters_stamped_and_unqueued()
    {
        var seriesId = SeedSeries();
        var (chapterId, _) = SeedChapter(seriesId, 1m, withFile: false, removedAt: DateTime.UtcNow.AddDays(-1));
        await ArchiveAsync(seriesId);
        var fake = new FakeSource { Name = "fake" };
        _listing = () => [fake.Chapter(1)];

        var result = await RestoreAsync(seriesId);

        Assert.Equal(0, result.Queued);
        using var db = _db.NewContext();
        Assert.NotNull(db.Chapters.Single(c => c.Id == chapterId).FileRemovedAt);
        Assert.Empty(db.DownloadQueue.Where(q => q.SeriesId == seriesId));
    }

    // ---- controller ----

    private SeriesController Controller(MakiDbContext db, MakiPermission permissions) => new(
        localizer: new TestLocalizer(),
        db: db,
        coverService: new CoverService(null!, new AppPaths(_tmp), new FakeAppSettings(), NullLogger<CoverService>.Instance),
        chapterSyncService: null!,
        cbzLinkService: null!,
        relinkPlanner: null!,
        seriesCreation: null!,
        seriesRename: null!,
        metadataRefresh: null!,
        downloadQueue: null!,
        downloadBatches: null!,
        appSettings: null!,
        kavitaScans: null!,
        scrobbler: null!,
        stats: new StatsEventService(db),
        mangaBakaStore: null!,
        similarSeries: null!,
        recommendationFeedback: null!,
        readingProfiles: null!,
        readingTimeEstimates: null!,
        sourceAvailability: null!,
        currentUser: new TestCurrentUser(1, permissions: permissions),
        userSettings: null!,
        notifications: new RecordingNotifications(),
        locales: new TestUserLocaleResolver(),
        logger: NullLogger<SeriesController>.Instance);

    [Fact]
    public async Task ArchivePreview_counts_the_chapters_restore_would_queue()
    {
        var seriesId = SeedSeries(configure: s => s.ArchivedAt = DateTime.UtcNow);
        SeedChapter(seriesId, 1m);
        SeedChapter(seriesId, 2m, withFile: false, removedAt: DateTime.UtcNow);
        SeedChapter(seriesId, 3m, withFile: false, removedAt: DateTime.UtcNow);
        SeedChapter(seriesId, 4m, withFile: false, wanted: false, removedAt: DateTime.UtcNow);
        SeedChapter(seriesId, 5m, withFile: false);

        using var db = _db.NewContext();
        var ok = Assert.IsType<OkObjectResult>(
            await Controller(db, MakiPermission.ArchiveSeries).ArchivePreview(seriesId, default));
        var preview = Assert.IsType<SeriesController.ArchivePreviewDto>(ok.Value);

        Assert.Equal(2, preview.RedownloadableChapters);
        Assert.True(preview.FreeableBytes > 0);
        Assert.Equal(0, preview.OtherActiveReaders);
        Assert.IsType<NotFoundResult>(await Controller(db, MakiPermission.ArchiveSeries).ArchivePreview(9999, default));
    }

    [Fact]
    public async Task Archive_with_removeFiles_is_forbidden_without_DeleteSeries()
    {
        var seriesId = SeedSeries();
        var (_, path) = SeedChapter(seriesId, 1m);

        using (var db = _db.NewContext())
        {
            var result = await Controller(db, MakiPermission.ArchiveSeries)
                .Archive(seriesId, new SeriesController.ArchiveSeriesRequest(RemoveFiles: true), Service(db), default);
            Assert.IsType<ForbidResult>(result);
        }

        Assert.Null(Load(seriesId).ArchivedAt);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Restore_with_redownload_is_forbidden_without_DownloadChapters()
    {
        var seriesId = SeedSeries(configure: s => s.ArchivedAt = DateTime.UtcNow);
        SeedChapter(seriesId, 1m, withFile: false, removedAt: DateTime.UtcNow);

        using (var db = _db.NewContext())
        {
            var result = await Controller(db, MakiPermission.ArchiveSeries)
                .Restore(seriesId, new SeriesController.RestoreSeriesRequest(RedownloadRemoved: true), Service(db), default);
            Assert.IsType<ForbidResult>(result);
        }

        Assert.NotNull(Load(seriesId).ArchivedAt);
        using var check = _db.NewContext();
        Assert.Empty(check.DownloadQueue.Where(q => q.SeriesId == seriesId));
    }

    [Fact]
    public async Task ArchiveBulk_answers_one_outcome_per_id()
    {
        var idle = SeedSeries("Idle");
        var busy = SeedSeries("Busy");
        SeedQueueRow(busy, SeedChapter(busy, 1m, withFile: false).ChapterId, QueueStatus.Importing);

        using var db = _db.NewContext();
        var ok = Assert.IsType<OkObjectResult>(await Controller(db, MakiPermission.ArchiveSeries)
            .ArchiveBulk(new SeriesController.BulkArchiveSeriesRequest([idle, busy]), Service(db), default));

        var results = JsonSerializer.SerializeToElement(ok.Value).GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(idle, results[0].GetProperty("seriesId").GetInt32());
        Assert.True(results[0].GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, results[0].GetProperty("code").ValueKind);
        Assert.Equal(busy, results[1].GetProperty("seriesId").GetInt32());
        Assert.False(results[1].GetProperty("ok").GetBoolean());
        Assert.False(results[1].GetProperty("notFound").GetBoolean());
        Assert.Equal("error.series.activeDownloadArchive", results[1].GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(results[1].GetProperty("error").GetString()));
        Assert.False(results[1].GetProperty("permanentDeleteAvailable").GetBoolean());
    }

    // ---- fakes ----

    private sealed class ScriptedMover(Func<MoveOutcome> outcome) : RecycleBinMover
    {
        public override MoveOutcome Move(string source, string target) => outcome();
    }

    /// <summary>Cancels the request's token right after the first query that reads <c>table</c>.</summary>
    private sealed class CancelOnQuery(string table, CancellationTokenSource request) : DbCommandInterceptor
    {
        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains($"\"{table}\"", StringComparison.Ordinal))
            {
                request.Cancel();
            }

            return ValueTask.FromResult(result);
        }
    }

    // ---- permission ----

    [Fact]
    public void ArchiveSeries_is_its_own_bit_that_admin_implies_and_delete_does_not()
    {
        Assert.Equal(1 << 12, (int)MakiPermission.ArchiveSeries);
        Assert.True(MakiPermission.Admin.Grants(MakiPermission.ArchiveSeries));
        Assert.True(MakiPermission.ArchiveSeries.Grants(MakiPermission.ArchiveSeries));
        Assert.False(MakiPermission.DeleteSeries.Grants(MakiPermission.ArchiveSeries));
        Assert.True(MakiPermissions.AllNonAdmin.Grants(MakiPermission.ArchiveSeries));
    }

    [Fact]
    public void The_migration_grants_ArchiveSeries_to_exactly_the_users_who_hold_DeleteSeries()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(connection).Options;

        void MigrateTo(string? target)
        {
            using var db = new MakiDbContext(options);
            db.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(target);
        }

        MigrateTo("20261025092942_HealthOperationDismissedAt");
        var held = new Dictionary<int, int>
        {
            [101] = 4,
            [102] = 4 | 8,
            [103] = 8,
            [104] = 1,
            [105] = 0,
        };
        foreach (var (id, permissions) in held)
        {
            InsertUser(connection, id, permissions);
        }

        MigrateTo(null);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Id, Permissions FROM AspNetUsers ORDER BY Id";
        using var rows = read.ExecuteReader();
        var after = new Dictionary<int, int>();
        while (rows.Read())
        {
            after[rows.GetInt32(0)] = rows.GetInt32(1);
        }

        Assert.Equal(4 | 4096, after[101]);
        Assert.Equal(4 | 8 | 4096, after[102]);
        Assert.Equal(8, after[103]);
        Assert.Equal(1, after[104]);
        Assert.Equal(0, after[105]);
    }

    /// <summary>
    /// Raw SQL on purpose: the model of the schema before this migration is not the current one. Every
    /// required column the test does not care about gets the empty value of its type.
    /// </summary>
    private static void InsertUser(SqliteConnection connection, int id, int permissions)
    {
        var columns = new List<string>();
        var values = new List<string>();
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(AspNetUsers)";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                var type = reader.GetString(2);
                var required = reader.GetInt32(3) == 1 && reader.IsDBNull(4);
                string? value = name switch
                {
                    "Id" => id.ToString(),
                    "UserName" or "NormalizedUserName" => $"'user{id}'",
                    "Permissions" => permissions.ToString(),
                    _ when !required => null,
                    _ when type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) => "0",
                    _ => "''",
                };
                if (value is not null)
                {
                    columns.Add($"\"{name}\"");
                    values.Add(value);
                }
            }
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO AspNetUsers ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})";
        insert.ExecuteNonQuery();
    }
}
