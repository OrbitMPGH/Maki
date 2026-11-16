using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Security;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="RelatedWorkWatchService"/>: a relation the dump newly lists for a library series becomes
/// a notification for that series' readers, once, and never on the pass that first sees the series.
/// </summary>
public sealed class RelatedWorkWatchServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DumpDbBuilder _dump = new();
    private readonly FakeAppSettings _settings = new();
    private readonly RecordingInbox _inbox = new();

    public void Dispose()
    {
        _dump.Dispose();
        _db.Dispose();
    }

    private Task RunAsync()
    {
        var options = new MangaBakaDumpOptions(_dump.Path, Path.GetTempPath());
        var service = new RelatedWorkWatchService(
            _db.ScopeFactory(),
            _settings,
            new MangaBakaLocalStore(options, NullLogger<MangaBakaLocalStore>.Instance),
            new InboxAudienceResolver(_db.ScopeFactory()),
            _inbox,
            NullLogger<RelatedWorkWatchService>.Instance);
        return service.RunAsync();
    }

    private int LibrarySeries(int mangaBakaId, string title) =>
        _db.SeedSeries(title, configure: s => s.MangaBakaId = mangaBakaId);

    private void Reads(int userId, int seriesId)
    {
        using var db = _db.NewContext();
        db.ReadingStates.Add(new ReadingState
        {
            UserId = userId, SeriesId = seriesId, Title = "x", MaxChapter = 1,
            LastProgressAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private void SetRelations(long id, string column, string json)
    {
        using var conn = _dump.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE series SET {column} = $json WHERE id = $id";
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task The_first_pass_records_the_relations_and_announces_nothing()
    {
        _dump.AddSeries(1, "Berserk", sequels: "[2]");
        _dump.AddSeries(2, "Berserk Sequel");
        var reader = _db.SeedUser("reader");
        Reads(reader, LibrarySeries(1, "Berserk"));

        await RunAsync();

        Assert.Empty(_inbox.Raised);
        Assert.Equal("""{"1":[2]}""", await _settings.GetAsync(SettingKeys.DiscoverRelatedSeen));
    }

    [Fact]
    public async Task A_new_sequel_notifies_the_series_readers_once()
    {
        _dump.AddSeries(1, "Berserk");
        var reader = _db.SeedUser("reader");
        var other = _db.SeedUser("other");
        var seriesId = LibrarySeries(1, "Berserk");
        Reads(reader, seriesId);
        await RunAsync();

        _dump.AddSeries(3, "Berserk Next");
        SetRelations(1, "relationships_sequel", "[3]");
        await RunAsync();
        await RunAsync();

        var (type, message, audience) = Assert.Single(_inbox.Raised);
        Assert.Equal(InboxEventType.NewRelatedWork, type);
        Assert.Equal(InboxAudience.User(reader), audience);
        Assert.Equal("inbox.relatedWork", message.Key);
        Assert.Equal("sequel", message.Params!["kind"]);
        Assert.Equal("Berserk Next", message.Params!["title"]);
        Assert.Equal(seriesId, message.SeriesId);
        Assert.Equal("/discover?open=3", message.Url);
        Assert.DoesNotContain(_inbox.Raised, r => r.Audience == InboxAudience.User(other));
    }

    [Fact]
    public async Task A_series_added_later_does_not_announce_relations_it_already_had()
    {
        _dump.AddSeries(1, "Berserk");
        _dump.AddSeries(5, "Vagabond", sequels: "[6]");
        _dump.AddSeries(6, "Vagabond Sequel");
        _db.SeedUser("admin");
        LibrarySeries(1, "Berserk");
        await RunAsync();

        LibrarySeries(5, "Vagabond");
        await RunAsync();

        Assert.Empty(_inbox.Raised);
    }

    [Fact]
    public async Task A_related_work_already_in_the_library_is_not_announced()
    {
        _dump.AddSeries(1, "Berserk");
        _dump.AddSeries(3, "Berserk Next");
        _db.SeedUser("admin");
        LibrarySeries(1, "Berserk");
        LibrarySeries(3, "Berserk Next");
        await RunAsync();

        SetRelations(1, "relationships_spin_off", "[3]");
        await RunAsync();

        Assert.Empty(_inbox.Raised);
    }

    [Fact]
    public async Task Content_ceiling_and_root_folder_grants_narrow_the_audience()
    {
        _dump.AddSeries(1, "Berserk");
        var seriesId = LibrarySeries(1, "Berserk");
        var adult = _db.SeedUser("adult", MakiPermission.None, maxContentRating: "erotica");
        var safe = _db.SeedUser("safe", MakiPermission.None, maxContentRating: "safe");
        var restricted = _db.SeedUser("restricted", MakiPermission.None, allRootFolders: false);
        Reads(adult, seriesId);
        Reads(safe, seriesId);
        Reads(restricted, seriesId);
        await RunAsync();

        _dump.AddSeries(4, "Berserk Side Story", contentRating: "erotica");
        SetRelations(1, "relationships_side_story", "[4]");
        await RunAsync();

        var (_, message, audience) = Assert.Single(_inbox.Raised);
        Assert.Equal(InboxAudience.User(adult), audience);
        Assert.Equal("sideStory", message.Params!["kind"]);
    }

    [Fact]
    public async Task More_than_a_handful_of_new_works_become_one_summary()
    {
        _dump.AddSeries(1, "Berserk");
        var reader = _db.SeedUser("reader");
        Reads(reader, LibrarySeries(1, "Berserk"));
        await RunAsync();

        var ids = Enumerable.Range(10, RelatedWorkWatchService.MaxSingleNotifications + 1).ToList();
        foreach (var id in ids)
        {
            _dump.AddSeries(id, $"Spin-off {id}");
        }

        SetRelations(1, "relationships_spin_off", $"[{string.Join(",", ids)}]");
        await RunAsync();

        var (_, message, _) = Assert.Single(_inbox.Raised);
        Assert.Equal("inbox.relatedWorks", message.Key);
        Assert.Equal(ids.Count, message.Params!["count"]);
    }
}
