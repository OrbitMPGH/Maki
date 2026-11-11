using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class VolumeFirstGateTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly StoppedClock _clock = new(Now);
    private readonly VolumeFirstSearches _searches;

    public VolumeFirstGateTests()
    {
        _searches = new VolumeFirstSearches(_clock);
        _settings.Set(SettingKeys.DownloadsVolumeFirst, "true");
    }

    public void Dispose() => _db.Dispose();

    /// <summary>A series with chapters 1 to 12, four to a volume.</summary>
    private int Series(bool volumes = true)
    {
        var id = _db.SeedSeries();
        using var db = _db.NewContext();
        for (var n = 1; n <= 12; n++)
        {
            db.Chapters.Add(new Chapter { SeriesId = id, Number = n, Volume = volumes ? (n + 3) / 4 : null });
        }

        db.SaveChanges();
        return id;
    }

    private async Task<VolumeHold> CheckAsync(int seriesId)
    {
        using var db = _db.NewContext();
        var torrents = new TorrentUpgradeService(db, null!, null!, null!, _settings, TimeProvider.System, null!,
            NullLogger<TorrentUpgradeService>.Instance);
        return await new VolumeFirstGate(db, _settings, _searches, torrents, _clock)
            .CheckAsync(seriesId, CancellationToken.None);
    }

    private void Proposal(int seriesId, TimeSpan age, string title = "S v01-02 (Digital)",
        TorrentProposalStatus status = TorrentProposalStatus.Pending, bool volumeFirst = true)
    {
        using var db = _db.NewContext();
        db.TorrentProposals.Add(new TorrentProposal
        {
            SeriesId = seriesId, ReleaseGuid = Guid.NewGuid().ToString(), Title = title, Status = status,
            CreatedAtUtc = Now - age, VolumeFirst = volumeFirst
        });
        db.SaveChanges();
    }

    private void TorrentItem(int seriesId, QueueStatus status, TimeSpan age, string title = "S v01-02 (Digital)",
        bool volumeFirst = true)
    {
        using var db = _db.NewContext();
        db.DownloadQueue.Add(new DownloadQueueItem
        {
            SeriesId = seriesId, Protocol = AcquisitionProtocol.Torrent, Status = status, Title = title,
            QueuedAt = Now - age,
            UpgradeInfoJson = new TorrentUpgradeInfo { VolumeFirst = volumeFirst }.Serialize()
        });
        db.SaveChanges();
    }

    private static Chapter Numbered(decimal n) => new() { Number = n };

    [Fact]
    public async Task Nothing_is_held_when_the_setting_is_off()
    {
        _settings.Set(SettingKeys.DownloadsVolumeFirst, "false");
        var id = Series();
        Proposal(id, TimeSpan.FromHours(1));
        using var _ = _searches.Begin(id);

        Assert.False((await CheckAsync(id)).Active);
    }

    [Fact]
    public async Task A_running_search_holds_every_chapter_until_it_ends()
    {
        var id = Series();
        var search = _searches.Begin(id);

        var hold = await CheckAsync(id);
        Assert.Equal(VolumeHoldReason.Searching, hold.Reason);
        Assert.True(hold.Holds(Numbered(500)));

        search.Dispose();
        search.Dispose();
        Assert.False((await CheckAsync(id)).Active);
        Assert.False(_searches.IsRunning(id));
    }

    [Fact]
    public async Task A_search_that_never_ends_stops_holding_at_the_deadline()
    {
        var id = Series();
        _searches.Begin(id);

        _clock.Now += VolumeFirstSearches.Deadline - TimeSpan.FromMinutes(1);
        Assert.True((await CheckAsync(id)).Active);

        _clock.Now += TimeSpan.FromMinutes(2);
        Assert.False((await CheckAsync(id)).Active);
        _clock.Now = Now;
    }

    [Fact]
    public async Task An_older_search_ending_does_not_end_a_newer_one()
    {
        var id = Series();
        var first = _searches.Begin(id);
        using var second = _searches.Begin(id);

        first.Dispose();

        Assert.True(_searches.IsRunning(id));
    }

    [Fact]
    public async Task A_series_whose_search_never_ran_is_not_held()
    {
        var id = Series();

        Assert.False((await CheckAsync(id)).Active);
    }

    [Fact]
    public async Task A_pending_proposal_holds_only_the_chapters_its_volumes_cover()
    {
        var id = Series();
        Proposal(id, TimeSpan.FromHours(1));

        var hold = await CheckAsync(id);
        Assert.Equal(VolumeHoldReason.Proposal, hold.Reason);
        Assert.Equal(8m, hold.Through);
        Assert.Equal(Now + VolumeFirstGate.ProposalHold - TimeSpan.FromHours(1), hold.Until);
        Assert.True(hold.Holds(Numbered(8)));
        Assert.False(hold.Holds(Numbered(9)));
    }

    [Fact]
    public async Task A_release_whose_volumes_cannot_be_placed_holds_every_chapter()
    {
        var id = Series(volumes: false);
        Proposal(id, TimeSpan.FromHours(1));

        var hold = await CheckAsync(id);
        Assert.Equal(VolumeHoldReason.Proposal, hold.Reason);
        Assert.Null(hold.Through);
        Assert.True(hold.Holds(Numbered(12)));
    }

    [Fact]
    public async Task A_proposal_holds_for_two_days_and_no_longer()
    {
        var recent = Series();
        Proposal(recent, TimeSpan.FromHours(47));
        var old = Series();
        Proposal(old, TimeSpan.FromHours(49));

        Assert.True((await CheckAsync(recent)).Active);
        Assert.False((await CheckAsync(old)).Active);
    }

    [Fact]
    public async Task An_answered_proposal_holds_nothing()
    {
        var id = Series();
        Proposal(id, TimeSpan.FromHours(1), status: TorrentProposalStatus.Dismissed);

        Assert.False((await CheckAsync(id)).Active);
    }

    [Theory]
    [InlineData(QueueStatus.Queued, true)]
    [InlineData(QueueStatus.Downloading, true)]
    [InlineData(QueueStatus.Importing, true)]
    [InlineData(QueueStatus.AwaitingImport, true)]
    [InlineData(QueueStatus.Completed, false)]
    [InlineData(QueueStatus.Failed, false)]
    [InlineData(QueueStatus.Cancelled, false)]
    public async Task A_release_still_downloading_holds_the_chapters_it_covers(QueueStatus status, bool holds)
    {
        var id = Series();
        TorrentItem(id, status, TimeSpan.FromHours(1));

        var hold = await CheckAsync(id);
        Assert.Equal(holds, hold.Active);
        if (holds)
        {
            Assert.Equal(VolumeHoldReason.Downloading, hold.Reason);
            Assert.Equal(8m, hold.Through);
        }
    }

    [Fact]
    public async Task A_bulk_add_waiting_for_the_job_holds_every_chapter_for_a_day()
    {
        var id = Series();
        using (var db = _db.NewContext())
        {
            db.Series.Single(s => s.Id == id).VolumeFirstPendingAt = Now - TimeSpan.FromHours(23);
            db.SaveChanges();
        }

        var hold = await CheckAsync(id);
        Assert.Equal(VolumeHoldReason.Searching, hold.Reason);
        Assert.Equal(Now + TimeSpan.FromHours(1), hold.Until);
        Assert.True(hold.Holds(Numbered(500)));

        _clock.Now += TimeSpan.FromHours(2);
        Assert.False((await CheckAsync(id)).Active);
        _clock.Now = Now;
    }

    [Fact]
    public async Task An_ordinary_upgrade_proposal_holds_nothing()
    {
        var id = Series();
        Proposal(id, TimeSpan.FromHours(1), volumeFirst: false);

        Assert.False((await CheckAsync(id)).Active);
    }

    [Fact]
    public async Task An_ordinary_upgrade_download_holds_nothing()
    {
        var id = Series();
        TorrentItem(id, QueueStatus.Downloading, TimeSpan.FromHours(1), volumeFirst: false);

        Assert.False((await CheckAsync(id)).Active);
    }

    [Fact]
    public async Task A_download_stops_holding_after_three_days()
    {
        var id = Series();
        TorrentItem(id, QueueStatus.Downloading, TimeSpan.FromHours(73));

        Assert.False((await CheckAsync(id)).Active);
    }
}
