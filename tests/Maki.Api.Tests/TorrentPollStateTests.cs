using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// When the completed-download poll may skip its queue read: after an empty read, until a grab or
/// until <see cref="TorrentPollState.RecheckWhileIdle"/> has passed.
/// </summary>
public class TorrentPollStateTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();

    [Fact]
    public void Nothing_is_skipped_before_a_read_has_come_back_empty()
    {
        Assert.False(new TorrentPollState(_clock).CanSkip);
    }

    [Fact]
    public void An_empty_read_is_skipped_until_a_grab()
    {
        var state = new TorrentPollState(_clock);
        state.MarkIdle(state.Generation);
        Assert.True(state.CanSkip);

        state.TorrentAdded();

        Assert.False(state.CanSkip);
    }

    [Fact]
    public void A_grab_during_the_read_keeps_the_next_poll_reading()
    {
        var state = new TorrentPollState(_clock);
        var before = state.Generation;

        state.TorrentAdded();
        state.MarkIdle(before);

        Assert.False(state.CanSkip);
    }

    [Fact]
    public void The_queue_is_read_again_after_the_recheck_interval()
    {
        var state = new TorrentPollState(_clock);
        state.MarkIdle(state.Generation);

        _clock.Now += TorrentPollState.RecheckWhileIdle;

        Assert.False(state.CanSkip);
    }
}
