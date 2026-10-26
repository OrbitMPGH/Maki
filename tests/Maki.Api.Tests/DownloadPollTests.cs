using Maki.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// How long the download cooldown poll sleeps: five seconds while the queue has work, otherwise
/// until the next cooldown or pause ends, at most a minute.
/// </summary>
public class DownloadPollTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private readonly TestDb _db = new();
    private readonly DownloadPauseService _pauses;
    private readonly DownloadQueueService _queue;
    private readonly DownloadWorkerHostedService _worker;

    public DownloadPollTests()
    {
        var clock = new Clock(T0);
        _pauses = new DownloadPauseService(new FakeAppSettings(), clock);
        _queue = new DownloadQueueService(
            _db.ScopeFactory(), clock, Sources.SingleChapterResolver(null, "fake"),
            NullLogger<DownloadQueueService>.Instance, _pauses);
        _worker = new DownloadWorkerHostedService(
            _queue,
            new DownloadBatchNotifier(
                new RecordingNotifications(), new RecordingInbox(), new TestLocalizer(),
                new TestUserLocaleResolver(), clock, NullLogger<DownloadBatchNotifier>.Instance),
            _db.ScopeFactory(), NullLogger<DownloadWorkerHostedService>.Instance, _pauses);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Before_any_claim_has_come_back_empty_the_poll_stays_short()
    {
        Assert.True(_queue.Busy);
        Assert.Equal(TimeSpan.FromSeconds(5), await _worker.NextPollDelayAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_queue_sleeps_for_a_minute()
    {
        Assert.Null(await _queue.ClaimNextAsync());

        Assert.False(_queue.Busy);
        Assert.Equal(TimeSpan.FromMinutes(1), await _worker.NextPollDelayAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_idle_poll_wakes_when_a_cooldown_lifts()
    {
        _queue.RestoreCooldown("fake", T0.UtcDateTime.AddSeconds(20));
        Assert.Null(await _queue.ClaimNextAsync());

        Assert.Equal(TimeSpan.FromSeconds(20), await _worker.NextPollDelayAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_idle_poll_wakes_when_a_pause_reaches_its_resume_time()
    {
        await _pauses.PauseAsync(null, T0.UtcDateTime.AddSeconds(30));
        Assert.Null(await _queue.ClaimNextAsync());

        Assert.Equal(TimeSpan.FromSeconds(30), await _worker.NextPollDelayAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_cooldown_lifting_sooner_than_the_short_tick_still_waits_the_short_tick()
    {
        _queue.RestoreCooldown("fake", T0.UtcDateTime.AddSeconds(1));
        Assert.Null(await _queue.ClaimNextAsync());

        Assert.Equal(TimeSpan.FromSeconds(5), await _worker.NextPollDelayAsync(CancellationToken.None));
    }
}
