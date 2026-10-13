using Microsoft.Extensions.Logging.Abstractions;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// The pause gate on <see cref="DownloadQueueService.ClaimNextAsync"/>: what it stops, what it leaves
/// alone, when it lifts and that it outlives the service holding it.
/// </summary>
public class DownloadPauseTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly TestDb _db = new();
    private readonly Clock _clock = new(T0);
    private readonly FakeAppSettings _settings = new();
    private readonly DownloadPauseService _pauses;
    private readonly DownloadQueueService _queue;

    public DownloadPauseTests()
    {
        _pauses = new DownloadPauseService(_settings, _clock);
        _queue = new DownloadQueueService(
            _db.ScopeFactory(), _clock, Sources.SingleChapterResolver(null, "fake"),
            NullLogger<DownloadQueueService>.Instance, _pauses);
    }

    public void Dispose() => _db.Dispose();

    private int SeedItem(string sourceName, QueueStatus status = QueueStatus.Queued, int sortOrder = 0)
    {
        var seriesId = _db.SeedSeries(mappings:
            [new SourceMapping { SourceName = sourceName, SourceSeriesId = "s", Url = $"https://{sourceName}.test", Priority = 1 }]);

        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();

        var item = new DownloadQueueItem
        {
            SeriesId = seriesId,
            ChapterId = chapter.Id,
            Protocol = AcquisitionProtocol.Scraper,
            Status = status,
            SourceMappingId = db.SourceMappings.Single(m => m.SeriesId == seriesId).Id,
            SortOrder = sortOrder,
            QueuedAt = T0.UtcDateTime
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private QueueStatus StatusOf(int id)
    {
        using var db = _db.NewContext();
        return db.DownloadQueue.Single(q => q.Id == id).Status;
    }

    [Fact]
    public async Task Nothing_is_paused_by_default()
    {
        var id = SeedItem("a");

        Assert.True((await _pauses.ActiveAsync()).IsEmpty);
        Assert.Equal(id, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_global_pause_claims_nothing_and_changes_no_status()
    {
        var id = SeedItem("a");
        await _pauses.PauseAsync(null, null);

        Assert.Null(await _queue.ClaimNextAsync());
        Assert.Equal(QueueStatus.Queued, StatusOf(id));
    }

    [Fact]
    public async Task Resuming_makes_the_queue_claimable_again()
    {
        var id = SeedItem("a");
        await _pauses.PauseAsync(null, null);
        await _pauses.ResumeAsync(null);

        Assert.Equal(id, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_source_pause_skips_that_source_and_still_claims_the_others()
    {
        SeedItem("paused", sortOrder: 0);
        var other = SeedItem("warm", sortOrder: 1);
        await _pauses.PauseAsync("paused", null);

        Assert.Equal(other, await _queue.ClaimNextAsync());
        Assert.Null(await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_source_pause_skips_rate_limited_rows_of_that_source_too()
    {
        SeedItem("paused", QueueStatus.RateLimited, sortOrder: 0);
        var other = SeedItem("warm", QueueStatus.RateLimited, sortOrder: 1);
        await _pauses.PauseAsync("paused", null);

        Assert.Equal(other, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task Resuming_one_source_leaves_the_global_pause_alone()
    {
        SeedItem("a");
        await _pauses.PauseAsync(null, null);
        await _pauses.PauseAsync("a", null);
        await _pauses.ResumeAsync("a");

        var state = await _pauses.ActiveAsync();
        Assert.NotNull(state.All);
        Assert.Empty(state.Sources);
        Assert.Null(await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task An_item_already_in_flight_is_unaffected_by_a_pause()
    {
        var running = SeedItem("a", sortOrder: 0);
        var waiting = SeedItem("a", sortOrder: 1);
        Assert.Equal(running, await _queue.ClaimNextAsync());

        await _pauses.PauseAsync(null, null);

        Assert.Equal(QueueStatus.FetchingPages, StatusOf(running));
        Assert.Null(await _queue.ClaimNextAsync());
        Assert.Equal(QueueStatus.Queued, StatusOf(waiting));
        Assert.Equal(0, await _queue.SweepOrphanedAsync());
        Assert.Equal(QueueStatus.FetchingPages, StatusOf(running));
    }

    [Fact]
    public async Task A_pause_lifts_by_itself_at_its_resume_time()
    {
        var id = SeedItem("a");
        await _pauses.PauseAsync(null, T0.UtcDateTime.AddHours(1));

        _clock.Now = T0.AddMinutes(59);
        Assert.Null(await _queue.ClaimNextAsync());

        _clock.Now = T0.AddHours(1);
        Assert.True((await _pauses.ActiveAsync()).IsEmpty);
        Assert.Equal(id, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_source_pause_lifts_by_itself_at_its_resume_time()
    {
        var id = SeedItem("a");
        await _pauses.PauseAsync("a", T0.UtcDateTime.AddMinutes(30));
        Assert.Null(await _queue.ClaimNextAsync());

        _clock.Now = T0.AddMinutes(31);

        Assert.Equal(id, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task Expire_clears_a_lapsed_pause_once_and_reports_it()
    {
        await _pauses.PauseAsync("a", T0.UtcDateTime.AddMinutes(30));
        await _pauses.PauseAsync("b", null);
        Assert.False(await _pauses.ExpireAsync());

        _clock.Now = T0.AddMinutes(31);

        Assert.True(await _pauses.ExpireAsync());
        Assert.False(await _pauses.ExpireAsync());
        Assert.Equal(["b"], (await _pauses.ActiveAsync()).Sources.Keys);
    }

    [Fact]
    public async Task A_pause_survives_rebuilding_the_service()
    {
        var id = SeedItem("a");
        await _pauses.PauseAsync("a", T0.UtcDateTime.AddHours(2));
        await _pauses.PauseAsync(null, null);

        var rebuilt = new DownloadPauseService(_settings, _clock);
        var state = await rebuilt.ActiveAsync();

        Assert.NotNull(state.All);
        Assert.Equal(T0.UtcDateTime.AddHours(2), state.Sources["a"].Until);
        var queue = new DownloadQueueService(
            _db.ScopeFactory(), _clock, Sources.SingleChapterResolver(null, "fake"),
            NullLogger<DownloadQueueService>.Instance, rebuilt);
        Assert.Null(await queue.ClaimNextAsync());
        Assert.Equal(QueueStatus.Queued, StatusOf(id));
    }

    [Fact]
    public async Task Resuming_everything_removes_the_stored_setting()
    {
        await _pauses.PauseAsync(null, null);
        await _pauses.ResumeAsync(null);

        Assert.Null(await _settings.GetAsync(SettingKeys.DownloadPause));
    }

    [Fact]
    public async Task A_corrupt_stored_value_reads_as_nothing_paused()
    {
        var id = SeedItem("a");
        _settings.Set(SettingKeys.DownloadPause, "{not json");

        Assert.True((await _pauses.ActiveAsync()).IsEmpty);
        Assert.Equal(id, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task Resuming_a_source_matches_its_name_the_way_pausing_does()
    {
        await _pauses.PauseAsync("MangaDex", null);
        var controller = new Maki.Api.Controllers.QueueController(
            new TestLocalizer(), _db.NewContext(), _queue, null!, _pauses,
            new Maki.Core.Sources.SourceRegistry([new FakeSource { Name = "MangaDex" }]), null!,
            new Maki.Api.Hubs.EventBroadcaster(new NoopHubContext(), _db.ScopeFactory()), null!,
            NullLogger<Maki.Api.Controllers.QueueController>.Instance);

        await controller.Resume("mangadex", CancellationToken.None);

        Assert.True((await _pauses.ActiveAsync()).IsEmpty);
    }

    [Fact]
    public async Task A_pause_is_answered_even_when_its_broadcast_fails()
    {
        var controller = new Maki.Api.Controllers.QueueController(
            new TestLocalizer(), _db.NewContext(), _queue, null!, _pauses,
            new Maki.Core.Sources.SourceRegistry([new FakeSource { Name = "MangaDex" }]), null!,
            new Maki.Api.Hubs.EventBroadcaster(new NoopHubContext(), new ThrowingScopeFactory()), null!,
            NullLogger<Maki.Api.Controllers.QueueController>.Instance);

        var result = await controller.Pause(new Maki.Api.Dtos.QueuePauseRequestDto(Source: null, ResumeAt: null), CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        Assert.NotNull((await _pauses.ActiveAsync()).All);
    }

    private sealed class ThrowingScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() =>
            throw new InvalidOperationException("database unavailable");
    }

    [Fact]
    public async Task A_pause_change_reaches_everyone_who_may_manage_the_queue()
    {
        var admin = _db.SeedUser("admin", Maki.Core.Security.MakiPermission.Admin);
        var manager = _db.SeedUser("manager", Maki.Core.Security.MakiPermission.ManageDownloadQueue);
        _db.SeedUser("reader", Maki.Core.Security.MakiPermission.None);
        _db.SeedUser("gone", Maki.Core.Security.MakiPermission.ManageDownloadQueue, configure: u => u.Disabled = true);
        var hub = new RecordingHubContext();

        await new Maki.Api.Hubs.EventBroadcaster(hub, _db.ScopeFactory()).QueuePauseChanged(new { });

        Assert.Equal(
            new[] { Maki.Api.Hubs.EventsHub.UserGroup(admin), Maki.Api.Hubs.EventsHub.UserGroup(manager) }.Order(),
            hub.Groups.Order());
    }

    private sealed class RecordingHubContext : Microsoft.AspNetCore.SignalR.IHubContext<Maki.Api.Hubs.EventsHub>
    {
        public List<string> Groups { get; } = [];
        public Microsoft.AspNetCore.SignalR.IHubClients Clients => new RecordingClients(this);
        Microsoft.AspNetCore.SignalR.IGroupManager Microsoft.AspNetCore.SignalR.IHubContext<Maki.Api.Hubs.EventsHub>.Groups =>
            throw new NotSupportedException();

        private sealed class RecordingClients(RecordingHubContext owner) : Microsoft.AspNetCore.SignalR.IHubClients
        {
            private static readonly Microsoft.AspNetCore.SignalR.IClientProxy Proxy = new NoopProxy();
            public Microsoft.AspNetCore.SignalR.IClientProxy All => Proxy;
            public Microsoft.AspNetCore.SignalR.IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public Microsoft.AspNetCore.SignalR.IClientProxy Client(string connectionId) => Proxy;
            public Microsoft.AspNetCore.SignalR.IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
            public Microsoft.AspNetCore.SignalR.IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public Microsoft.AspNetCore.SignalR.IClientProxy User(string userId) => Proxy;
            public Microsoft.AspNetCore.SignalR.IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;

            public Microsoft.AspNetCore.SignalR.IClientProxy Group(string groupName)
            {
                owner.Groups.Add(groupName);
                return Proxy;
            }

            public Microsoft.AspNetCore.SignalR.IClientProxy Groups(IReadOnlyList<string> groupNames)
            {
                owner.Groups.AddRange(groupNames);
                return Proxy;
            }
        }

        private sealed class NoopProxy : Microsoft.AspNetCore.SignalR.IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) => Task.CompletedTask;
        }
    }
}
