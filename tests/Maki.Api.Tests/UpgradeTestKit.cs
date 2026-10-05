using System.Collections.Concurrent;
using System.Net;
using Maki.Api.Configuration;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace Maki.Api.Tests;

/// <summary>Quartz hands a job a context it only reads the token and job data off.</summary>
internal sealed class TestJobContext(JobDataMap? data = null, CancellationToken ct = default) : IJobExecutionContext
{
    public CancellationToken CancellationToken => ct;
    public IScheduler Scheduler => throw new NotSupportedException();
    public ITrigger Trigger => throw new NotSupportedException();
    public ICalendar? Calendar => null;
    public bool Recovering => false;
    public TriggerKey RecoveringTriggerKey => throw new NotSupportedException();
    public int RefireCount => 0;
    public JobDataMap MergedJobDataMap => data ?? [];
    public IJobDetail JobDetail => throw new NotSupportedException();
    public IJob JobInstance => throw new NotSupportedException();
    public DateTimeOffset FireTimeUtc => DateTimeOffset.UtcNow;
    public DateTimeOffset? ScheduledFireTimeUtc => null;
    public DateTimeOffset? PreviousFireTimeUtc => null;
    public DateTimeOffset? NextFireTimeUtc => null;
    public string FireInstanceId => "test";
    public object? Result { get; set; }
    public TimeSpan JobRunTime => TimeSpan.Zero;
    public void Put(object key, object objectValue) { }
    public object? Get(object key) => null;
}

/// <summary>Serves a generated PNG of one size for every request and records what was asked for.</summary>
internal sealed class PngHttpFactory : IHttpClientFactory
{
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 2400;
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public ConcurrentQueue<string> Requested { get; } = new();

    public HttpClient CreateClient(string name) => new(new Handler(this));

    private sealed class Handler(PngHttpFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            owner.Requested.Enqueue(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(owner.Status);
            if (owner.Status == HttpStatusCode.OK)
            {
                response.Content = new ByteArrayContent(TestQuality.Png(owner.Width, owner.Height));
            }

            return Task.FromResult(response);
        }
    }
}

/// <summary>
/// A library with one series, two source mappings ("agg", an aggregator the files came from, and
/// "official") and an upgrade profile, on a temp root folder, with the config dir pointed inside it.
/// </summary>
internal sealed class UpgradeWorld : IDisposable
{
    public const string Agg = "agg";
    public const string Official = "official";

    private readonly string? _priorEnv;

    public UpgradeWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "maki-upgrade-" + Guid.NewGuid().ToString("N")[..8]);
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", Path.Combine(Root, "config"));
        Registry = new SourceRegistry([
            new FakeSource { Name = Agg, Kind = SourceKind.Aggregator, OnGetPages = c => AggPages(c) },
            new FakeSource
            {
                Name = Official, Kind = SourceKind.Official, OnGetPages = c => OfficialPages(c),
                OnListChapters = id => OfficialListing(id)
            }
        ]);
        Queue = new DownloadQueueService(Db.ScopeFactory(), TimeProvider.System,
            Sources.Resolver(Registry, Availability), NullLogger<DownloadQueueService>.Instance);
    }

    public TestDb Db { get; } = new();
    public FakeAppSettings Settings { get; } = new();
    public string Root { get; }
    public string Library => Path.Combine(Root, "library");
    public SourceRegistry Registry { get; }
    public DownloadQueueService Queue { get; }
    public PngHttpFactory Http { get; } = new();
    public RecordingInbox Inbox { get; } = new();
    public RecordingNotifications Notifications { get; } = new();
    public ReaderArchiveCache Archives { get; } = new(NullLogger<ReaderArchiveCache>.Instance);

    public Func<SourceChapter, ChapterPages> AggPages { get; set; } = _ => new ChapterPages([]);
    public Func<SourceChapter, ChapterPages> OfficialPages { get; set; } = UrlPages(20);
    public Func<string, IReadOnlyList<SourceChapter>> OfficialListing { get; set; } = _ => [];

    public SourceAvailability Availability => new(Settings, Registry);

    public int SeriesId { get; private set; }
    public int ProfileId { get; private set; }
    public int AggMappingId { get; private set; }
    public int OfficialMappingId { get; private set; }

    public static Func<SourceChapter, ChapterPages> UrlPages(int count) => c => new ChapterPages(
        [.. Enumerable.Range(0, count).Select(i => new PageRequest($"https://{c.SourceName}.test/{c.SourceChapterId}/{i}.png"))]);

    public static Func<SourceChapter, ChapterPages> InlinePages(int count, int width, int height = 30)
    {
        var png = TestQuality.Png(width, height);
        return c => new ChapterPages(
            [.. Enumerable.Range(0, count).Select(i => new PageRequest($"https://{c.SourceName}.test/{i}.png", Data: png))]);
    }

    /// <summary>Profile: default tier order, cutoff Official, upgrades on, a width format worth 10.</summary>
    public UpgradeWorld Seed(Action<UpgradeProfile>? profile = null, Action<Series>? series = null, int minWidth = 1400)
    {
        using var db = Db.NewContext();
        var format = new QualityFormat
        {
            Name = "High resolution",
            Conditions = [new FormatCondition(FormatConditionType.MinWidth, minWidth.ToString(), true, false)]
        };
        db.QualityFormats.Add(format);
        db.SaveChanges();

        var p = new UpgradeProfile
        {
            Name = "Test", Cutoff = QualityTier.Official, UpgradesEnabled = true,
            FormatScores = [new FormatScore(format.Id, 10)]
        };
        UpgradeProfileDefaults.Normalise(p);
        profile?.Invoke(p);
        db.UpgradeProfiles.Add(p);

        var root = new RootFolder { Path = Library };
        db.RootFolders.Add(root);
        db.SaveChanges();

        var s = new Series
        {
            Title = "Series", SortTitle = "series", FolderName = "Series", RootFolderId = root.Id,
            UpgradeProfileId = p.Id, Added = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var agg = new SourceMapping { SourceName = Agg, SourceSeriesId = "agg-series", Priority = 1 };
        var official = new SourceMapping { SourceName = Official, SourceSeriesId = "official-series", Priority = 2 };
        s.SourceMappings.AddRange([agg, official]);
        series?.Invoke(s);
        db.Series.Add(s);
        db.SaveChanges();

        SeriesId = s.Id;
        ProfileId = p.Id;
        AggMappingId = agg.Id;
        OfficialMappingId = official.Id;
        return this;
    }

    /// <summary>
    /// A chapter whose file came from "agg" (800px, 20 pages, measured 30 days ago), linked to both
    /// mappings as "a{n}" and "o{n}". <paramref name="onDisk"/> writes a real archive for it.
    /// </summary>
    public (int ChapterId, int? FileId) Chapter(decimal number, Action<ChapterFile>? file = null, bool withFile = true,
        bool wanted = true, bool onDisk = false, int pages = 20, int width = 800, string extension = "cbz")
    {
        using var db = Db.NewContext();
        int? fileId = null;
        if (withFile)
        {
            var relative = $"Series/Series {number:000}.{extension}";
            var row = new ChapterFile
            {
                SeriesId = SeriesId, RelativePath = relative, SourceName = Agg, SourceChapterId = $"a{number}",
                Tier = QualityTier.Aggregator, PageCount = pages, MedianWidth = width, MedianHeight = width * 3 / 2,
                ImageFormat = "png", Size = 1000, DateAdded = DateTime.UtcNow.AddDays(-30),
                MeasuredAtUtc = DateTime.UtcNow.AddDays(-30)
            };
            file?.Invoke(row);
            if (onDisk)
            {
                var path = Path.Combine(Library, relative);
                TestQuality.WriteCbz(path, pages, width, width * 3 / 2);
                row.Size = new FileInfo(path).Length;
            }

            db.ChapterFiles.Add(row);
            db.SaveChanges();
            fileId = row.Id;
        }

        var chapter = new Chapter
        {
            SeriesId = SeriesId, Number = number, NumberRaw = number.ToString(), Wanted = wanted, ChapterFileId = fileId
        };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        db.ChapterSourceLinks.AddRange(
            new ChapterSourceLink { ChapterId = chapter.Id, SourceMappingId = AggMappingId, SourceChapterId = $"a{number}" },
            new ChapterSourceLink { ChapterId = chapter.Id, SourceMappingId = OfficialMappingId, SourceChapterId = $"o{number}" });
        db.SaveChanges();
        return (chapter.Id, fileId);
    }

    public SourceProbeService Probes() => new(
        new PageDownloader(Http, Queue, TimeProvider.System, NullLogger<PageDownloader>.Instance), Queue, new AppPaths(),
        NullLogger<SourceProbeService>.Instance);

    public DownloadBatchNotifier Batches() => new(
        Notifications, Inbox, new TestLocalizer(), new TestUserLocaleResolver(), TimeProvider.System,
        NullLogger<DownloadBatchNotifier>.Instance);

    public UpgradeScanService Scanner(MakiDbContext db, DownloadBatchNotifier batches) => new(
        db, new UpgradeEvaluationService(db, TestQuality.Create(Registry)), Registry, Availability, Probes(), Queue,
        batches, Settings, TimeProvider.System, NullLogger<UpgradeScanService>.Instance);

    public ChapterDownloadProcessor Processor(MakiDbContext db, DownloadBatchNotifier batches, StatsEventService? stats = null) => new(
        db, Registry, Sources.Resolver(Registry, Availability),
        new PageDownloader(new StubHttpClientFactory(""), Queue, TimeProvider.System, NullLogger<PageDownloader>.Instance),
        new EventBroadcaster(new NoopHubContext(), Db.ScopeFactory()),
        new AppPaths(),
        new KavitaScanService(new KavitaClient(new StubHttpClientFactory("{}")), Settings,
            Db.ScopeFactory(), NullLogger<KavitaScanService>.Instance),
        Queue, Inbox, stats ?? new StatsEventService(db), Notifications, batches,
        Availability, Archives, new NamingService(Settings), TestQuality.Create(Registry), new TestLocalizer(),
        new TestUserLocaleResolver(), NullLogger<ChapterDownloadProcessor>.Instance);

    /// <summary>An upgrade row for the chapter's file from "official", as the scan would queue it.</summary>
    public int QueueUpgrade(int chapterId, int fileId, string sourceChapterId = "o1")
    {
        using var db = Db.NewContext();
        var file = db.ChapterFiles.AsNoTracking().Single(f => f.Id == fileId);
        var info = new UpgradeInfo
        {
            ChapterFileId = fileId, ProfileId = ProfileId, ProfileVersion = 1,
            Before = UpgradeEvaluator.Snapshot(file, 0),
            Predicted = new QualitySnapshot { Tier = "official", SourceName = Official, MedianWidth = 160 }
        };
        var item = new DownloadQueueItem
        {
            SeriesId = SeriesId, ChapterId = chapterId, SourceMappingId = OfficialMappingId,
            PreferredMappingId = OfficialMappingId, SourceChapterId = sourceChapterId, QueuedAt = DateTime.UtcNow,
            Origin = DownloadOrigin.Upgrade, UpgradeInfoJson = info.Serialize()
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    /// <summary>A user's pick of <paramref name="mappingId"/> for a chapter that has a file, as DownloadFrom queues it.</summary>
    public int QueueForced(int chapterId, int fileId, int mappingId, string sourceChapterId, bool ignoreGuards = false,
        int profileId = 0)
    {
        using var db = Db.NewContext();
        var file = db.ChapterFiles.AsNoTracking().Single(f => f.Id == fileId);
        var info = new UpgradeInfo
        {
            ChapterFileId = fileId, ProfileId = profileId, Force = true, IgnoreGuards = ignoreGuards,
            Before = UpgradeEvaluator.Snapshot(file, 0)
        };
        var item = new DownloadQueueItem
        {
            SeriesId = SeriesId, ChapterId = chapterId, SourceMappingId = mappingId, PreferredMappingId = mappingId,
            SourceChapterId = sourceChapterId, QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.Manual,
            UpgradeInfoJson = info.Serialize()
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    public async Task<DownloadOutcome> ProcessAsync(int itemId, StatsEventService? stats = null)
    {
        using var db = Db.NewContext();
        using var batches = Batches();
        return await Processor(db, batches, stats).ProcessAsync(itemId, CancellationToken.None);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        Db.Dispose();
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
