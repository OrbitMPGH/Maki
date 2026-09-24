using Maki.Api.Services;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>Server-side stitching of reader time reports into sittings.</summary>
public sealed class ReadingSessionServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 1, 20, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();
    private readonly int _user;

    public ReadingSessionServiceTests() => _user = _db.SeedUser("sitter");

    public void Dispose() => _db.Dispose();

    private async Task Record(int userId, int seconds, DateTime now, bool completed = false, int? scopeUser = null)
    {
        await using var db = scopeUser is { } s ? _db.NewContext(s) : _db.NewContext(userId);
        await new ReadingSessionService(db).RecordAsync(userId, seconds, completed, now, CancellationToken.None);
    }

    private List<ReadingSession> Sessions(int userId)
    {
        using var db = _db.NewContext();
        return db.ReadingSessions.Where(s => s.UserId == userId).OrderBy(s => s.StartedAt).ToList();
    }

    [Fact]
    public async Task AReportWithinTheGapExtendsTheSitting()
    {
        await Record(_user, 60, T0);
        // 9 minutes of silence between the first sitting's end and the second report's start.
        await Record(_user, 60, T0.AddMinutes(9).AddSeconds(60));

        var session = Assert.Single(Sessions(_user));
        Assert.Equal(T0.AddSeconds(-60), session.StartedAt);
        Assert.Equal(T0.AddMinutes(10), session.EndedAt);
        Assert.Equal(120, session.ActiveSeconds);
    }

    [Fact]
    public async Task AReportPastTheGapStartsANewSitting()
    {
        await Record(_user, 60, T0);
        await Record(_user, 60, T0.AddMinutes(ReadingSessionService.GapMinutes + 1).AddSeconds(60));

        var sessions = Sessions(_user);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(60, sessions[1].ActiveSeconds);
        Assert.Equal(T0.AddMinutes(11), sessions[1].StartedAt);
    }

    [Fact]
    public async Task CompletionsCountOnTheSitting()
    {
        await Record(_user, 60, T0, completed: true);
        await Record(_user, 60, T0.AddMinutes(1), completed: false);
        await Record(_user, 60, T0.AddMinutes(2), completed: true);
        // No time, but close enough to the sitting to be credited to it.
        await Record(_user, 0, T0.AddMinutes(3), completed: true);

        var session = Assert.Single(Sessions(_user));
        Assert.Equal(3, session.ChaptersCompleted);
        Assert.Equal(180, session.ActiveSeconds);
    }

    [Fact]
    public async Task AnOverlappingReportPullsTheStartEarlier()
    {
        await Record(_user, 60, T0);
        // A chunk arriving late for the ten minutes just before the first report started.
        await Record(_user, 600, T0.AddSeconds(-60));

        var session = Assert.Single(Sessions(_user));
        Assert.Equal(T0.AddSeconds(-660), session.StartedAt);
        Assert.Equal(T0, session.EndedAt);
        Assert.Equal(660, session.ActiveSeconds);
        Assert.True(session.ActiveSeconds <= (session.EndedAt - session.StartedAt).TotalSeconds);
    }

    [Fact]
    public async Task AZeroSecondCompletionWithNoNearbySittingIsDropped()
    {
        await Record(_user, 0, T0, completed: true);

        Assert.Empty(Sessions(_user));
    }

    [Fact]
    public async Task UsersNeverShareASitting()
    {
        var other = _db.SeedUser("other");

        await Record(_user, 60, T0);
        await Record(other, 60, T0.AddMinutes(1));

        Assert.Equal(60, Assert.Single(Sessions(_user)).ActiveSeconds);
        Assert.Equal(60, Assert.Single(Sessions(other)).ActiveSeconds);
    }

    [Fact]
    public async Task RecordsForTheTargetUserUnderAnotherUsersScope()
    {
        var other = _db.SeedUser("other");
        await Record(_user, 60, T0);

        // The ambient scope is another user's, so a filtered read would miss the existing sitting
        // and open a second one.
        await Record(_user, 60, T0.AddMinutes(2), scopeUser: other);

        var session = Assert.Single(Sessions(_user));
        Assert.Equal(120, session.ActiveSeconds);
        Assert.Empty(Sessions(other));
    }
}
