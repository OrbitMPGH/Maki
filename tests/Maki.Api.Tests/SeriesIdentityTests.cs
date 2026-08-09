using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Removing a series and adding it back must produce one history, not two.
/// <para>
/// The FK is severed to NULL on delete, so <see cref="StatsEvent.SeriesKey"/> is the only identity
/// that survives. These cover both halves: the key being written and matched, and the adoption that
/// re-points orphaned rows at the new series so they get their cover and link back.
/// </para>
/// </summary>
public sealed class SeriesIdentityTests : IDisposable
{
    private const int TestUser = 1;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SeriesIdentityService Identity() =>
        new(_db.NewContext(), NullLogger<SeriesIdentityService>.Instance);

    private ActivityStatsService Activity() =>
        new(_db.NewContext(), new FakeAppSettings(),
            new StoppedClock(new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero)));

    private static readonly DateOnly Y26Start = new(2026, 1, 1);
    private static readonly DateOnly Y26End = new(2026, 12, 31);

    /// <summary>Writes an event the way a live one lands: keyed, pointed at its series.</summary>
    private void AddEvent(StatsEventType type, DateTime utc, string key, string title,
        int value = 1, int? seriesId = null, int? userId = null)
    {
        using var db = _db.NewContext();
        db.StatsEvents.Add(new StatsEvent
        {
            Type = type,
            Timestamp = utc,
            UserId = userId,
            SeriesId = seriesId,
            SeriesKey = key,
            SeriesTitle = title,
            Value = value
        });
        db.SaveChanges();
    }

    // ---- the key itself ----

    [Fact]
    public void ProviderIdBeatsTitleSoARenameDoesNotSplitAHistory()
    {
        var before = new Series { Title = "Old Name", MangaBakaId = 42 };
        var after = new Series { Title = "Completely Different", MangaBakaId = 42 };

        Assert.Equal(SeriesIdentity.For(before), SeriesIdentity.For(after));
        Assert.Equal("mb:42", SeriesIdentity.For(after));
    }

    [Fact]
    public void ProviderIdsFallThroughInPriorityOrder()
    {
        Assert.Equal("md:abc", SeriesIdentity.For(new Series { Title = "T", MangaDexUuid = "abc" }));
        Assert.Equal("al:7", SeriesIdentity.For(new Series { Title = "T", AniListId = 7 }));
        Assert.Equal("mal:9", SeriesIdentity.For(new Series { Title = "T", MalId = 9 }));
        Assert.Equal("t:t", SeriesIdentity.For(new Series { Title = "T" }));
    }

    [Fact]
    public void TitleKeyIgnoresCasePunctuationAccentsAndSpacing()
    {
        Assert.Equal(
            SeriesIdentity.ForTitle("Naruto: The Seventh Hokage"),
            SeriesIdentity.ForTitle("naruto - the  seventh hokage!"));
        Assert.Equal(SeriesIdentity.ForTitle("Pokemon"), SeriesIdentity.ForTitle("Pokémon"));
    }

    [Fact]
    public void DifferentTitlesStayApart()
    {
        Assert.NotEqual(
            SeriesIdentity.ForTitle("Naruto"),
            SeriesIdentity.ForTitle("Naruto: The Seventh Hokage"));
    }

    // ---- adoption ----

    [Fact]
    public async Task ReAddedSeriesAdoptsTheOrphanedHalfOfItsHistory()
    {
        // Read 40 chapters, then the series is deleted: the FK is severed, the key is not.
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            "mb:42", "Naruto", 40, seriesId: null, userId: TestUser);
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            "mb:42", "Naruto", 1_800, seriesId: null, userId: TestUser);

        var readded = _db.SeedSeries("Naruto", configure: s =>
        {
            s.MangaBakaId = 42;
            s.CoverPath = "covers/naruto.jpg";
        });
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            "mb:42", "Naruto", 5, seriesId: readded, userId: TestUser);

        using (var db = _db.NewContext())
        {
            await Identity().AdoptOrphansAsync(db.Series.Single(s => s.Id == readded), CancellationToken.None);
        }

        var stats = await Activity().StatsAsync(TestUser, Y26Start, Y26End, 0, CancellationToken.None);

        // One entry holding both halves, and it carries the live series' cover and link.
        var read = Assert.Single(stats.TopRead);
        Assert.Equal(45, read.Count);
        Assert.Equal(readded, read.SeriesId);
        Assert.NotNull(read.CoverUrl);

        var time = Assert.Single(stats.TopByTime);
        Assert.Equal(1_800, time.Seconds);
        Assert.Equal(readded, time.SeriesId);
    }

    [Fact]
    public async Task AdoptionMatchesOnTitleWhenTheOrphanHasNoProviderId()
    {
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            SeriesIdentity.ForTitle("Naruto: The Seventh Hokage"), "Naruto: The Seventh Hokage", 12,
            userId: TestUser);

        // The re-added copy has a provider id the orphan never had — the title key is the bridge.
        var readded = _db.SeedSeries("naruto  the seventh hokage", configure: s => s.MangaBakaId = 42);
        using (var db = _db.NewContext())
        {
            await Identity().AdoptOrphansAsync(db.Series.Single(s => s.Id == readded), CancellationToken.None);
        }

        using var check = _db.NewContext();
        Assert.All(check.StatsEvents.ToList(), e => Assert.Equal(readded, e.SeriesId));

        // The orphan's title key must not survive the adopt: if it did, the aggregation would
        // still group it apart from the live series' own events, which carry the new mb: key.
        Assert.All(check.StatsEvents.ToList(), e => Assert.Equal("mb:42", e.SeriesKey));
    }

    [Fact]
    public async Task AdoptedOrphanMergesIntoOneStatsEntryDespiteDifferentKeys()
    {
        // Orphan predates the provider id, so it only ever got a title key.
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            SeriesIdentity.ForTitle("Naruto"), "Naruto", 1_200, userId: TestUser);

        var readded = _db.SeedSeries("Naruto", configure: s => s.MangaBakaId = 42);
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            "mb:42", "Naruto", 600, seriesId: readded, userId: TestUser);

        using (var db = _db.NewContext())
        {
            await Identity().AdoptOrphansAsync(db.Series.Single(s => s.Id == readded), CancellationToken.None);
        }

        var stats = await Activity().StatsAsync(TestUser, Y26Start, Y26End, 0, CancellationToken.None);

        var time = Assert.Single(stats.TopByTime);
        Assert.Equal(1_800, time.Seconds);
        Assert.Equal(readded, time.SeriesId);
    }

    [Fact]
    public async Task AdoptionNeverTouchesADifferentSeries()
    {
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            "mb:99", "Bleach", 10, userId: TestUser);

        var naruto = _db.SeedSeries("Naruto", configure: s => s.MangaBakaId = 42);
        using (var db = _db.NewContext())
        {
            await Identity().AdoptOrphansAsync(db.Series.Single(s => s.Id == naruto), CancellationToken.None);
        }

        using var check = _db.NewContext();
        Assert.Null(check.StatsEvents.Single().SeriesId);
    }

    [Fact]
    public async Task AdoptionLeavesAlreadyLinkedEventsAlone()
    {
        var other = _db.SeedSeries("Other", configure: s => s.MangaBakaId = 42);
        // Same key, but already attached to a live series: adoption must not steal it.
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            "mb:42", "Other", 10, seriesId: other, userId: TestUser);

        var readded = _db.SeedSeries("Readded", configure: s => s.MangaBakaId = 42);
        using (var db = _db.NewContext())
        {
            await Identity().AdoptOrphansAsync(db.Series.Single(s => s.Id == readded), CancellationToken.None);
        }

        using var check = _db.NewContext();
        Assert.Equal(other, check.StatsEvents.Single().SeriesId);
    }

    // ---- reading marks ----

    [Fact]
    public async Task TombstonedReadingMarkIsAdoptedSoProgressDoesNotRestart()
    {
        using (var db = _db.NewContext())
        {
            // Both keys null is exactly what a hard delete leaves behind.
            db.ReadingStates.Add(new ReadingState
            {
                UserId = TestUser,
                Title = "Naruto",
                MaxChapter = 200,
                MaxVolume = 20,
                Finished = true,
                LastProgressAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            db.SaveChanges();
        }

        var readded = _db.SeedSeries("Naruto", configure: s => s.MangaBakaId = 42);
        using (var db = _db.NewContext())
        {
            await Identity().AdoptOrphansAsync(db.Series.Single(s => s.Id == readded), CancellationToken.None);
        }

        using var check = _db.NewContext();
        var state = Assert.Single(check.ReadingStates.ToList());
        Assert.Equal(readded, state.SeriesId);
        Assert.Equal(200, state.MaxChapter);
        Assert.True(state.Finished);
    }

    [Fact]
    public async Task TombstoneMergesIntoALiveRowTakingTheFurtherMark()
    {
        var readded = _db.SeedSeries("Naruto", configure: s => s.MangaBakaId = 42);
        using (var db = _db.NewContext())
        {
            db.ReadingStates.AddRange(
                // Tombstone from before the delete: further along.
                new ReadingState
                {
                    UserId = TestUser, Title = "Naruto", MaxChapter = 200,
                    LastProgressAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                // A Kavita scan created a fresh row after the re-add, starting near zero.
                new ReadingState
                {
                    UserId = TestUser, SeriesId = readded, KavitaSeriesId = 7, Title = "Naruto",
                    MaxChapter = 3, LastProgressAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
                });
            db.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            await Identity().AdoptOrphansAsync(db.Series.Single(s => s.Id == readded), CancellationToken.None);
        }

        using var check = _db.NewContext();
        var state = Assert.Single(check.ReadingStates.ToList());
        Assert.Equal(200, state.MaxChapter);
        Assert.Equal(7, state.KavitaSeriesId);
        // Forward-only: the later timestamp wins even though the further mark came from the older row.
        Assert.Equal(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), state.LastProgressAt);
    }

    [Fact]
    public async Task OneUsersTombstoneIsNeverAdoptedIntoAnother()
    {
        var otherUser = _db.SeedUser("other");
        var readded = _db.SeedSeries("Naruto", configure: s => s.MangaBakaId = 42);
        using (var db = _db.NewContext())
        {
            db.ReadingStates.AddRange(
                new ReadingState
                {
                    UserId = TestUser, Title = "Naruto", MaxChapter = 200,
                    LastProgressAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new ReadingState
                {
                    UserId = otherUser, Title = "Naruto", MaxChapter = 5,
                    LastProgressAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)
                });
            db.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            await Identity().AdoptOrphansAsync(db.Series.Single(s => s.Id == readded), CancellationToken.None);
        }

        using var check = _db.NewContext();
        var states = check.ReadingStates.IgnoreQueryFilters().ToList();
        Assert.Equal(2, states.Count);
        Assert.Equal(200, states.Single(s => s.UserId == TestUser).MaxChapter);
        Assert.Equal(5, states.Single(s => s.UserId == otherUser).MaxChapter);
    }

    // ---- the one-time repair ----

    [Fact]
    public async Task RepairKeysUnkeyedRowsAndAdoptsThemIntoTheLiveSeries()
    {
        var live = _db.SeedSeries("Naruto", configure: s => s.MangaBakaId = 42);
        using (var db = _db.NewContext())
        {
            // Pre-migration shapes: no key at all, one orphaned and one still linked.
            db.StatsEvents.AddRange(
                new StatsEvent
                {
                    Type = StatsEventType.ChaptersRead, UserId = TestUser,
                    Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                    SeriesTitle = "Naruto", Value = 40
                },
                new StatsEvent
                {
                    Type = StatsEventType.ChaptersRead, UserId = TestUser,
                    Timestamp = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    SeriesId = live, SeriesTitle = "Naruto", Value = 5
                });
            db.SaveChanges();
        }

        await new SeriesIdentityRepairService(
            _db.NewContext(), Identity(), NullLogger<SeriesIdentityRepairService>.Instance)
            .RunOnceAsync();

        using var check = _db.NewContext();
        var rows = check.StatsEvents.ToList();
        Assert.All(rows, e => Assert.NotNull(e.SeriesKey));
        Assert.All(rows, e => Assert.Equal(live, e.SeriesId));
        // The linked row takes the series' provider key; the orphan took the title key and was
        // then adopted, which is what makes the two halves one entry.
        Assert.Contains(rows, e => e.SeriesKey == "mb:42");
    }

    [Fact]
    public async Task RepairRunsOnlyOnce()
    {
        var repair = new SeriesIdentityRepairService(
            _db.NewContext(), Identity(), NullLogger<SeriesIdentityRepairService>.Instance);
        await repair.RunOnceAsync();

        using (var db = _db.NewContext())
        {
            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.ChaptersRead,
                Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                SeriesTitle = "Later", Value = 1
            });
            db.SaveChanges();
        }

        await new SeriesIdentityRepairService(
            _db.NewContext(), Identity(), NullLogger<SeriesIdentityRepairService>.Instance)
            .RunOnceAsync();

        using var check = _db.NewContext();
        Assert.Null(check.StatsEvents.Single().SeriesKey);
    }

    [Fact]
    public async Task RepairRealignsKeysLeftMismatchedByThePreFixAdoption()
    {
        var live = _db.SeedSeries("Naruto", configure: s => s.MangaBakaId = 42);
        using (var db = _db.NewContext())
        {
            // The MarkerKey pass already ran on this install, before adoption started rewriting
            // SeriesKey: SeriesId got fixed up but the row still carries its old title key, so it
            // still shows up as a second, separate entry in the stats aggregation.
            db.AppConfig.Add(new AppConfigEntry
            {
                Key = SeriesIdentityRepairService.MarkerKey,
                Value = DateTime.UtcNow.ToString("O")
            });
            db.StatsEvents.AddRange(
                new StatsEvent
                {
                    Type = StatsEventType.ReadingTime, UserId = TestUser,
                    Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                    SeriesId = live, SeriesTitle = "Naruto",
                    SeriesKey = SeriesIdentity.ForTitle("Naruto"), Value = 1_200
                },
                new StatsEvent
                {
                    Type = StatsEventType.ReadingTime, UserId = TestUser,
                    Timestamp = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    SeriesId = live, SeriesTitle = "Naruto", SeriesKey = "mb:42", Value = 600
                });
            db.SaveChanges();
        }

        await new SeriesIdentityRepairService(
            _db.NewContext(), Identity(), NullLogger<SeriesIdentityRepairService>.Instance)
            .RunOnceAsync();

        using var check = _db.NewContext();
        Assert.All(check.StatsEvents.ToList(), e => Assert.Equal("mb:42", e.SeriesKey));

        var stats = await Activity().StatsAsync(TestUser, Y26Start, Y26End, 0, CancellationToken.None);
        var time = Assert.Single(stats.TopByTime);
        Assert.Equal(1_800, time.Seconds);
    }
}
