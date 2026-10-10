using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Metadata;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A series with no provider ids is keyed by its title in the stats log. Renaming it by hand must
/// carry its history to the new key, touch nobody else's rows, and leave adoption of a later re-add
/// working.
/// </summary>
public class SeriesMetadataIdentityTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task An_id_less_series_keeps_its_history_through_an_edit_and_a_reset()
    {
        var id = _db.SeedSeries("Old Name");
        var other = _db.SeedSeries("Old Name");
        SeedEvent(id, SeriesIdentity.ForTitle("Old Name"));
        SeedEvent(other, SeriesIdentity.ForTitle("Old Name"));

        await Edit(id, "New Name");

        Assert.Equal([SeriesIdentity.ForTitle("New Name")], Keys(id));
        Assert.Equal([SeriesIdentity.ForTitle("Old Name")], Keys(other));

        await using (var db = _db.NewContext())
        {
            await Controller(db).Reset(id, new SeriesMetadataController.ResetMetadataRequest(["title"]), CancellationToken.None);
        }

        // No provider to restore from, so the title and with it the key stay where the edit put them.
        await using var read = _db.NewContext();
        var series = await read.Series.SingleAsync(s => s.Id == id);
        Assert.Equal(SeriesIdentity.For(series), Assert.Single(Keys(id)));
        Assert.Equal([SeriesIdentity.ForTitle("Old Name")], Keys(other));
    }

    [Fact]
    public async Task A_series_with_provider_ids_keeps_its_key()
    {
        var id = _db.SeedSeries("Old Name", configure: s => s.MangaBakaId = 42);
        SeedEvent(id, "mb:42");

        await Edit(id, "New Name");

        Assert.Equal(["mb:42"], Keys(id));
    }

    [Fact]
    public async Task A_re_added_copy_under_the_new_title_still_adopts_the_history()
    {
        var id = _db.SeedSeries("Old Name");
        SeedEvent(id, SeriesIdentity.ForTitle("Old Name"));
        await Edit(id, "New Name");

        await using var db = _db.NewContext();
        await db.StatsEvents.ExecuteUpdateAsync(u => u.SetProperty(e => e.SeriesId, (int?)null));
        var readded = _db.SeedSeries("New Name");
        var series = await db.Series.SingleAsync(s => s.Id == readded);

        var (events, _) = await new SeriesIdentityService(db, NullLogger<SeriesIdentityService>.Instance)
            .AdoptOrphansAsync(series, CancellationToken.None);

        Assert.Equal(1, events);
        Assert.Equal([SeriesIdentity.ForTitle("New Name")], Keys(readded));
    }

    private async Task Edit(int id, string title)
    {
        await using var db = _db.NewContext();
        await Controller(db).Edit(
            id, new SeriesMetadataController.EditMetadataRequest(["title"], Title: title), CancellationToken.None);
    }

    private void SeedEvent(int seriesId, string key)
    {
        using var db = _db.NewContext();
        db.StatsEvents.Add(new StatsEvent
        {
            Type = StatsEventType.ChaptersRead,
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UserId = 1,
            SeriesId = seriesId,
            SeriesKey = key,
            SeriesTitle = "Old Name",
        });
        db.SaveChanges();
    }

    private List<string?> Keys(int seriesId)
    {
        using var db = _db.NewContext();
        return db.StatsEvents.Where(e => e.SeriesId == seriesId).Select(e => e.SeriesKey).ToList();
    }

    private SeriesMetadataController Controller(MakiDbContext db) =>
        new(new TestLocalizer(), db, new SeriesMetadataRefreshService([new NoProvider()], null!),
            new SeriesMetadataChangeLog(db, new RecordingInbox(), new RecordingNotifications(),
                new TestUserLocaleResolver(), new TestLocalizer(), TimeProvider.System,
                NullLogger<SeriesMetadataChangeLog>.Instance),
            new SeriesIdentityService(db, NullLogger<SeriesIdentityService>.Instance),
            new TestCurrentUser(1),
            new KavitaScanService(new KavitaClient(new StubHttpClientFactory("{}")), new FakeAppSettings(),
                _db.ScopeFactory(), NullLogger<KavitaScanService>.Instance));

    private sealed class NoProvider : IMetadataProvider
    {
        public string Name => "stub";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(null);
    }
}
