using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Covers #98: <c>ScrobbleService.NativePassAsync</c> opened a fresh, unrestricted DI scope and never
/// narrowed <see cref="Maki.Data.DataScope"/> to the user it was syncing, so the <c>ReadingStates</c>
/// query and the plan-to-read <c>Series</c> scan returned every user's rows. Each connected user's
/// trackers then got pushed progress for everyone's reading, not just their own.
/// </summary>
public class ScrobbleServiceTests
{
    private static ScrobbleService BuildService(TestDb db, FakeUserSettingsStore userSettings) =>
        new(
            db.ScopeFactory(),
            settings: null!,
            userSettings,
            kavitaUser: null!,
            kavita: null!,
            anilist: null!,
            mal: null!,
            mangaBaka: null!,
            kitsu: null!,
            NullLogger<ScrobbleService>.Instance);

    /// <summary>
    /// Two users, each with a granted root folder and a <see cref="ReadingState"/> on a series in it.
    /// Syncing user A must push only A's series, at A's chapter count, never B's.
    /// </summary>
    [Fact]
    public async Task NativePassAsync_OnlyPushesTheSyncingUsersOwnSeries()
    {
        using var db = new TestDb();
        var userA = db.SeedUser("reader-a", MakiPermission.None, allRootFolders: false);
        var userB = db.SeedUser("reader-b", MakiPermission.None, allRootFolders: false);

        var seriesA = db.SeedSeries("Series A", configure: s => s.MangaBakaId = 100);
        var seriesB = db.SeedSeries("Series B", configure: s => s.MangaBakaId = 200);

        using (var seed = db.NewContext())
        {
            var rootA = seed.Series.Single(s => s.Id == seriesA).RootFolderId;
            var rootB = seed.Series.Single(s => s.Id == seriesB).RootFolderId;
            seed.UserRootFolders.Add(new UserRootFolder { UserId = userA, RootFolderId = rootA });
            seed.UserRootFolders.Add(new UserRootFolder { UserId = userB, RootFolderId = rootB });

            seed.ReadingStates.Add(new ReadingState
            {
                UserId = userA, SeriesId = seriesA, Title = "Series A",
                MaxChapter = 5, LastProgressAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            seed.ReadingStates.Add(new ReadingState
            {
                UserId = userB, SeriesId = seriesB, Title = "Series B",
                MaxChapter = 7, LastProgressAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }

        var userSettings = new FakeUserSettingsStore(db);
        var service = BuildService(db, userSettings);
        var tracker = new FakeScrobbleTracker();

        await service.NativePassAsync(userA, [tracker], ownsKavita: false, CancellationToken.None);

        Assert.Contains(tracker.Pushes, p => p.RemoteId == "100" && p.Chapter == 5);
        Assert.DoesNotContain(tracker.Pushes, p => p.RemoteId == "200");
    }

    /// <summary>
    /// Plan-to-read listing (no <see cref="ReadingState"/> at all yet) must respect root-folder grants
    /// too: a series in a folder the syncing user cannot see must never be listed on their tracker.
    /// </summary>
    [Fact]
    public async Task NativePassAsync_PlanToRead_NeverListsSeriesOutsideGrantedRootFolders()
    {
        using var db = new TestDb();
        var userA = db.SeedUser("reader-a", MakiPermission.None, allRootFolders: false);
        var userB = db.SeedUser("reader-b", MakiPermission.None, allRootFolders: false);

        var seriesA = db.SeedSeries("Series A", configure: s => s.MangaBakaId = 100);
        var seriesC = db.SeedSeries("Series C (not A's)", configure: s => s.MangaBakaId = 300);

        using (var seed = db.NewContext())
        {
            var rootA = seed.Series.Single(s => s.Id == seriesA).RootFolderId;
            var rootC = seed.Series.Single(s => s.Id == seriesC).RootFolderId;
            seed.UserRootFolders.Add(new UserRootFolder { UserId = userA, RootFolderId = rootA });
            seed.UserRootFolders.Add(new UserRootFolder { UserId = userB, RootFolderId = rootC });
            seed.SaveChanges();
        }

        var userSettings = new FakeUserSettingsStore(db);
        await userSettings.SetAsync(userA, SettingKeys.ScrobblePlanToRead, "true");

        var service = BuildService(db, userSettings);
        var tracker = new FakeScrobbleTracker();

        await service.NativePassAsync(userA, [tracker], ownsKavita: false, CancellationToken.None);

        // Series A (granted) is listed as plan-to-read; Series C (userB's folder only) never is.
        Assert.Contains(tracker.Pushes, p => p.RemoteId == "100");
        Assert.DoesNotContain(tracker.Pushes, p => p.RemoteId == "300");
    }

    private sealed class FakeScrobbleTracker : IScrobbleTracker
    {
        public List<(string RemoteId, int Chapter, int Volume)> Pushes { get; } = [];

        public string Name => "mangabaka";
        public string Label => "MangaBaka";
        public bool UsesOAuth => false;

        public Task<bool> ConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> UsernameAsync(int userId, CancellationToken ct = default) =>
            Task.FromResult<string?>("fake");

        public Task<RemoteEntry> GetEntryAsync(int userId, string remoteId, CancellationToken ct = default) =>
            Task.FromResult(new RemoteEntry());

        public Task UpdateAsync(
            int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
            CancellationToken ct = default)
        {
            Pushes.Add((remoteId, chapter, volume));
            return Task.CompletedTask;
        }

        public Task UpdateRatingAsync(int userId, string remoteId, int score, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(
            int userId, string title, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScrobbleCandidate>>([]);

        public string EntryUrl(string remoteId) => $"https://example.test/{remoteId}";
    }

    private sealed class FakeUserSettingsStore(TestDb db) : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default)
        {
            using var context = db.NewContext();
            return Task.FromResult(context.UserSettings
                .Where(s => s.UserId == userId && s.Key == key)
                .Select(s => s.Value)
                .FirstOrDefault());
        }

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default)
        {
            using var context = db.NewContext();
            var row = context.UserSettings.FirstOrDefault(s => s.UserId == userId && s.Key == key);
            if (row is null)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    context.UserSettings.Add(new UserSetting { UserId = userId, Key = key, Value = value });
                }
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                context.UserSettings.Remove(row);
            }
            else
            {
                row.Value = value;
            }

            context.SaveChanges();
            return Task.CompletedTask;
        }
    }
}
