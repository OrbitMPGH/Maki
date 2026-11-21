using Maki.Api.Services;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>The "Series cards" setting: what is stored, and what "auto" picks.</summary>
public sealed class CardProgressTests : IDisposable
{
    private const int User = 1;
    private static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private void Add(int userId, StatsEventType type, params DateTime[] stamps)
    {
        using var db = _db.NewContext();
        foreach (var t in stamps)
            db.StatsEvents.Add(new StatsEvent { UserId = userId, Type = type, Timestamp = t, Value = 60 });
        db.SaveChanges();
    }

    private async Task<string> Auto(int userId = User, TimeZoneInfo? zone = null)
    {
        using var db = _db.NewContext();
        return await CardProgress.ResolveAutoAsync(db, userId, zone ?? TimeZoneInfo.Utc, Now, CancellationToken.None);
    }

    [Theory]
    [InlineData("downloads", "downloads")]
    [InlineData("reading", "reading")]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("ring", "auto")]
    public void NormalizeKeepsKnownValues(string? stored, string expected) =>
        Assert.Equal(expected, CardProgress.Normalize(stored));

    [Fact]
    public async Task NoReadingPicksDownloads() => Assert.Equal(CardProgress.Downloads, await Auto());

    [Fact]
    public async Task ThreeReaderDaysPickReading()
    {
        Add(User, StatsEventType.ReadingTime, Now.AddDays(-1), Now.AddDays(-5), Now.AddDays(-20));
        Assert.Equal(CardProgress.Reading, await Auto());
    }

    [Fact]
    public async Task SeveralSessionsOnOneDayCountOnce()
    {
        Add(User, StatsEventType.ReadingTime, Now.AddDays(-1), Now.AddDays(-1).AddHours(-2), Now.AddDays(-2));
        Assert.Equal(CardProgress.Downloads, await Auto());
    }

    [Fact]
    public async Task DaysAreTheReadersLocalDays()
    {
        // Three UTC dates (18th, 19th, 20th) but two evenings in New York (18th, 19th).
        var evening = new DateTime(2026, 11, 18, 23, 30, 0, DateTimeKind.Utc);
        Add(User, StatsEventType.ReadingTime, evening, evening.AddHours(1), evening.AddDays(1).AddHours(1));
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.Equal(CardProgress.Reading, await Auto());
        Assert.Equal(CardProgress.Downloads, await Auto(zone: newYork));
    }

    [Fact]
    public async Task ReadingOutsideTheWindowDoesNotCount()
    {
        Add(User, StatsEventType.ReadingTime, Now.AddDays(-1), Now.AddDays(-2), Now.AddDays(-31));
        Assert.Equal(CardProgress.Downloads, await Auto());
    }

    [Fact]
    public async Task OnlyTheReaderAndOnlyThisUserCount()
    {
        Add(User, StatsEventType.ChaptersRead, Now.AddDays(-1), Now.AddDays(-2), Now.AddDays(-3));
        var other = _db.SeedUser("other");
        Add(other, StatsEventType.ReadingTime, Now.AddDays(-1), Now.AddDays(-2), Now.AddDays(-3));
        Assert.Equal(CardProgress.Downloads, await Auto());
    }
}
