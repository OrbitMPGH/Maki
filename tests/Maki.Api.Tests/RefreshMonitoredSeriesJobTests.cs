using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The refresh-selection predicate: only a Completed series that already holds a chapter reaching
/// its known total is skipped; everything else (ongoing, unknown total, behind, no total) refreshes.
/// A series with no enabled mapping is never a candidate.
/// </summary>
public class RefreshMonitoredSeriesJobTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private int SeedWithChapters(
        SeriesStatus status, int? totalChapters, bool enabledMapping, params decimal[] chapterNumbers)
    {
        var mapping = new SourceMapping
        {
            SourceName = "fake", SourceSeriesId = "s", Url = "u", Enabled = enabledMapping
        };

        var seriesId = _db.SeedSeries(
            configure: s => { s.Status = status; s.TotalChapters = totalChapters; },
            mappings: mapping);

        using var db = _db.NewContext();
        foreach (var n in chapterNumbers)
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = n, Language = "en" });
        }
        db.SaveChanges();
        return seriesId;
    }

    private async Task<List<int>> Refreshable(params string[] disabledSources)
    {
        using var db = _db.NewContext();
        return await RefreshMonitoredSeriesJob.RefreshableSeriesIdsAsync(db, [.. disabledSources]);
    }

    [Fact]
    public async Task Series_whose_only_source_is_globally_disabled_is_not_a_candidate()
    {
        var id = SeedWithChapters(SeriesStatus.Ongoing, totalChapters: null, enabledMapping: true, 1m);

        Assert.Contains(id, await Refreshable());
        Assert.DoesNotContain(id, await Refreshable("fake"));
    }

    [Fact]
    public async Task Completed_and_caught_up_is_skipped()
    {
        var id = SeedWithChapters(SeriesStatus.Completed, totalChapters: 100, enabledMapping: true, 99m, 100m);

        Assert.DoesNotContain(id, await Refreshable());
    }

    [Fact]
    public async Task Completed_but_behind_is_refreshed()
    {
        var id = SeedWithChapters(SeriesStatus.Completed, totalChapters: 100, enabledMapping: true, 98m, 99m);

        Assert.Contains(id, await Refreshable());
    }

    [Fact]
    public async Task Ongoing_is_always_refreshed()
    {
        var id = SeedWithChapters(SeriesStatus.Ongoing, totalChapters: 5, enabledMapping: true, 5m, 6m);

        Assert.Contains(id, await Refreshable());
    }

    [Fact]
    public async Task Completed_with_unknown_total_is_refreshed()
    {
        var id = SeedWithChapters(SeriesStatus.Completed, totalChapters: null, enabledMapping: true, 10m);

        Assert.Contains(id, await Refreshable());
    }

    [Fact]
    public async Task No_enabled_mapping_is_never_a_candidate()
    {
        var id = SeedWithChapters(SeriesStatus.Ongoing, totalChapters: null, enabledMapping: false, 1m);

        Assert.DoesNotContain(id, await Refreshable());
    }
}

/// <summary>
/// The Smart gate in <see cref="RefreshMonitoredSeriesJob.RefreshSeriesAsync"/>. New chapters on a
/// Smart series must be discovered and wanted (so they count toward the series total) but not
/// queued — dripping them out against reading progress is what Smart mode is for, and this job
/// grabbing them all the moment they appear would defeat it.
/// <para>
/// This gate used to be implicit: <c>Chapter.MonitoredUnder</c> returned false for Smart, so new
/// chapters landed unmonitored and the enqueue predicate simply never matched them. They are wanted
/// now, so the gate has to be real.
/// </para>
/// </summary>
public class RefreshSmartGateTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DownloadBatchNotifier _batches = new(
        new RecordingNotifications(), new RecordingInbox(), new TestLocalizer(), new TestUserLocaleResolver(), TimeProvider.System,
        NullLogger<DownloadBatchNotifier>.Instance);

    public void Dispose()
    {
        _batches.Dispose();
        _db.Dispose();
    }

    private int Seed(NewChapterMonitorMode mode) => _db.SeedSeries(
        monitor: mode,
        mappings: new SourceMapping { SourceName = "fake", SourceSeriesId = "series", Url = "u", Enabled = true });

    /// <summary>The job resolves ChapterSyncService per series, so the scope has to supply one.</summary>
    private IServiceScopeFactory ScopeFactoryWith(ISource source, IAppSettings? settings = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddSingleton(new SourceRegistry([source]));
        services.AddSingleton(Sources.AllEnabled);
        services.AddSingleton(new SourceChapterListCache(
            TimeProvider.System, NullLogger<SourceChapterListCache>.Instance));
        services.AddSingleton(settings ?? new FakeAppSettings());
        services.AddSingleton(_ => new DownloadQueueService(
            _db.ScopeFactory(), TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance));
        services.AddLogging();
        services.AddScoped<ChapterSyncService>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private async Task<(List<Chapter> Chapters, int Queued)> RefreshAsync(
        NewChapterMonitorMode mode, int chapterCount = 2, IAppSettings? settings = null,
        RecordingNotifications? notifications = null)
    {
        var seriesId = Seed(mode);
        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => [.. Enumerable.Range(1, chapterCount).Select(n => fake.Chapter(n))]
        };

        var registry = new SourceRegistry([source]);
        var queue = new DownloadQueueService(
            _db.ScopeFactory(), TimeProvider.System, Sources.Resolver(registry),
            NullLogger<DownloadQueueService>.Instance);

        var job = new RefreshMonitoredSeriesJob(
            ScopeFactoryWith(source, settings), queue, notifications ?? new RecordingNotifications(), new RecordingInbox(),
            _batches, Sources.AllEnabled, new TestLocalizer(), new TestUserLocaleResolver(),
            NullLogger<RefreshMonitoredSeriesJob>.Instance);

        await job.RefreshSeriesAsync(seriesId, CancellationToken.None);

        using var db = _db.NewContext();
        return (
            [.. db.Chapters.Where(c => c.SeriesId == seriesId)],
            db.DownloadQueue.Count(q => q.SeriesId == seriesId));
    }

    [Fact]
    public async Task Smart_series_wants_new_chapters_but_queues_none()
    {
        var (chapters, queued) = await RefreshAsync(NewChapterMonitorMode.Smart);

        Assert.Equal(2, chapters.Count);
        Assert.All(chapters, c => Assert.True(c.Wanted));
        Assert.Equal(0, queued);
    }

    [Fact]
    public async Task Non_smart_series_queues_its_new_chapters()
    {
        var (chapters, queued) = await RefreshAsync(NewChapterMonitorMode.All);

        Assert.Equal(2, chapters.Count);
        Assert.Equal(2, queued);
    }

    [Fact]
    public async Task None_mode_wants_nothing_and_queues_nothing()
    {
        var (chapters, queued) = await RefreshAsync(NewChapterMonitorMode.None);

        Assert.All(chapters, c => Assert.False(c.Wanted));
        Assert.Equal(0, queued);
    }

    [Fact]
    public async Task A_burst_past_the_threshold_is_held_but_stays_wanted()
    {
        var notifications = new RecordingNotifications();

        var (chapters, queued) = await RefreshAsync(NewChapterMonitorMode.All, chapterCount: 6, notifications: notifications);

        Assert.Equal(6, chapters.Count);
        Assert.All(chapters, c => Assert.True(c.Wanted));
        Assert.Equal(0, queued);
        Assert.Contains("notify.chapters.held.body", Assert.Single(notifications.Sent).Message.Body);
    }

    [Fact]
    public async Task A_burst_at_the_threshold_is_queued()
    {
        var (_, queued) = await RefreshAsync(NewChapterMonitorMode.All, chapterCount: 5);

        Assert.Equal(5, queued);
    }

    [Fact]
    public async Task A_zero_threshold_never_holds()
    {
        var settings = new FakeAppSettings().Set(SettingKeys.MonitoringBulkHoldThreshold, "0");

        var (_, queued) = await RefreshAsync(NewChapterMonitorMode.All, chapterCount: 50, settings: settings);

        Assert.Equal(50, queued);
    }
}
