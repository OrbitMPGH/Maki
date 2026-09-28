using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Inbox;
using Maki.Core.Recommendations;
using Maki.Metadata.Catalogue;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="FollowedCreatorReleaseService"/>: series added to the catalogue since the last pass,
/// credited to someone a reader follows, become that reader's notifications.
/// </summary>
public sealed class FollowedCreatorReleaseServiceTests : IDisposable
{
    private static readonly int ThisYear = DateTime.UtcNow.Year;

    private readonly TestDb _db = new();
    private readonly DumpDbBuilder _dump = new();
    private readonly FakeAppSettings _settings = new();
    private readonly RecordingInbox _inbox = new();

    public void Dispose()
    {
        _dump.Dispose();
        _db.Dispose();
    }

    public FollowedCreatorReleaseServiceTests()
    {
        _dump.AddSeries(1, "Uzumaki", year: 1998, authorsJson: """["Junji Ito"]""");
        _dump.AddSeries(2, "Berserk", year: 1989, authorsJson: """["Kentaro Miura"]""");
    }

    /// <summary>A fresh service per pass: the credit index cache notices a changed dump by file stamp,
    /// which a test writing twice within one clock tick cannot rely on.</summary>
    private Task RunAsync()
    {
        var options = new MangaBakaDumpOptions(_dump.Path, Path.GetTempPath());
        var service = new FollowedCreatorReleaseService(
            _db.ScopeFactory(),
            _settings,
            new CatalogueIndexCache(options, NullLogger<CatalogueIndexCache>.Instance),
            new MangaBakaLocalStore(options, _settings, NullLogger<MangaBakaLocalStore>.Instance),
            _inbox,
            TimeProvider.System,
            NullLogger<FollowedCreatorReleaseService>.Instance);
        return service.RunAsync();
    }

    private int Follower(string name, string ceiling = "erotica", CatalogueCredit[]? follows = null)
    {
        var id = _db.SeedUser(name, maxContentRating: ceiling);
        _db.SetUserConfig(id, (SettingKeys.DiscoverFollowing,
            FollowedCreatorsSpec.Serialize(new FollowedCreatorsSpec(follows ?? []))));
        return id;
    }

    [Fact]
    public async Task The_first_pass_only_records_where_the_catalogue_is()
    {
        Follower("reader", follows: [new CatalogueCredit("Junji Ito")]);

        await RunAsync();

        Assert.Empty(_inbox.Raised);
        Assert.Equal("2", await _settings.GetAsync(SettingKeys.DiscoverFollowingWatermark));
    }

    [Fact]
    public async Task A_new_series_by_a_followed_creator_notifies_that_follower_once()
    {
        var reader = Follower("reader", follows: [new CatalogueCredit("Junji Ito")]);
        Follower("other", follows: [new CatalogueCredit("Kentaro Miura")]);
        await RunAsync();

        _dump.AddSeries(10, "Dark Colors", year: ThisYear, authorsJson: """["ITO, Junji"]""");
        await RunAsync();
        await RunAsync();

        var (type, message, audience) = Assert.Single(_inbox.Raised);
        Assert.Equal(InboxEventType.FollowedCreatorRelease, type);
        Assert.Equal(InboxAudience.User(reader), audience);
        Assert.Equal("inbox.followedRelease", message.Key);
        Assert.Equal("Dark Colors", message.Params!["title"]);
        Assert.Equal("/creator/Junji%20Ito?open=10", message.Url);
        Assert.Equal("10", await _settings.GetAsync(SettingKeys.DiscoverFollowingWatermark));
    }

    [Fact]
    public async Task A_transient_notification_write_failure_is_retried_once()
    {
        var reader = Follower("reader", follows: [new CatalogueCredit("Junji Ito")]);
        await RunAsync();

        _dump.AddSeries(10, "Dark Colors", year: ThisYear, authorsJson: """["Junji Ito"]""");
        _inbox.DurableRaiseFailures = 1;
        await RunAsync();

        var (_, _, audience) = Assert.Single(_inbox.Raised);
        Assert.Equal(InboxAudience.User(reader), audience);
        Assert.Equal("10", await _settings.GetAsync(SettingKeys.DiscoverFollowingWatermark));
    }

    [Fact]
    public async Task A_notification_write_that_keeps_failing_does_not_hold_back_the_watermark()
    {
        Follower("reader", follows: [new CatalogueCredit("Junji Ito")]);
        await RunAsync();

        _dump.AddSeries(10, "Dark Colors", year: ThisYear, authorsJson: """["Junji Ito"]""");
        _inbox.DurableRaiseFailures = int.MaxValue;
        await RunAsync();

        Assert.Empty(_inbox.Raised);
        Assert.Equal("10", await _settings.GetAsync(SettingKeys.DiscoverFollowingWatermark));
    }

    [Fact]
    public async Task Owned_old_and_over_the_ceiling_entries_are_left_out()
    {
        Follower("reader", ceiling: "safe", follows: [new CatalogueCredit("Junji Ito")]);
        _db.SeedSeries("Tomie", configure: s => s.MangaBakaId = 11);
        await RunAsync();

        _dump.AddSeries(11, "Tomie", year: ThisYear, authorsJson: """["Junji Ito"]""");
        _dump.AddSeries(12, "Backfilled Oneshot", year: 1995, authorsJson: """["Junji Ito"]""");
        _dump.AddSeries(13, "Explicit", year: ThisYear, contentRating: "erotica", authorsJson: """["Junji Ito"]""");
        _dump.AddSeries(14, "Announced", authorsJson: """["Junji Ito"]""");
        await RunAsync();

        var raised = Assert.Single(_inbox.Raised);
        Assert.Equal("Announced", raised.Message.Params!["title"]);
    }

    [Fact]
    public async Task A_role_limited_follow_ignores_the_name_in_other_roles()
    {
        Follower("reader", follows: [new CatalogueCredit("Kentaro Miura", CatalogueCredits.Artist)]);
        await RunAsync();

        _dump.AddSeries(20, "Written only", year: ThisYear, authorsJson: """["Kentaro Miura"]""");
        await RunAsync();

        Assert.Empty(_inbox.Raised);
    }

    [Fact]
    public async Task Many_new_titles_become_one_summary()
    {
        Follower("reader", follows: [new CatalogueCredit("Junji Ito"), new CatalogueCredit("Kentaro Miura")]);
        await RunAsync();

        for (var i = 0; i < FollowedCreatorReleaseService.MaxSingleNotifications + 1; i++)
        {
            _dump.AddSeries(30 + i, $"New {i}", year: ThisYear,
                authorsJson: i % 2 == 0 ? """["Junji Ito"]""" : """["Kentaro Miura"]""");
        }

        await RunAsync();

        var raised = Assert.Single(_inbox.Raised);
        Assert.Equal("inbox.followedReleases", raised.Message.Key);
        Assert.Equal(FollowedCreatorReleaseService.MaxSingleNotifications + 1, raised.Message.Params!["count"]);
    }
}
