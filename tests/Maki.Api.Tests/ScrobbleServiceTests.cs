using System.Globalization;
using System.Net;
using System.Text;
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
            volumeBoundaries: null!,
            anilist: null!,
            mal: null!,
            mangaBaka: null!,
            kitsu: null!,
            logger: NullLogger<ScrobbleService>.Instance);

    /// <summary>
    /// Builds a service whose <c>_trackers</c> array is all real (if mostly unused) instances -
    /// <see cref="ScrobbleService.FindTracker"/> throws on a null entry before it ever reaches the one
    /// the test cares about, so every slot needs a working object even when only <paramref
    /// name="mangaBaka"/> is exercised.
    /// </summary>
    private static ScrobbleService BuildService(
        TestDb db, FakeUserSettingsStore userSettings, MangaBakaTracker mangaBaka)
    {
        var noopHttp = new FakeHttpClientFactory(new HttpClient(new FakeHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))));
        var appSettings = new FakeAppSettings();
        var tokens = new FakeScrobbleTokenStore();
        var options = new ScrobbleTrackerOptions();

        return new ScrobbleService(
            db.ScopeFactory(),
            settings: null!,
            userSettings,
            kavitaUser: null!,
            kavita: null!,
            volumeBoundaries: null!,
            anilist: new AniListTracker(noopHttp, appSettings, tokens, options, NullLogger<AniListTracker>.Instance),
            mal: new MalTracker(noopHttp, appSettings, tokens, options, NullLogger<MalTracker>.Instance),
            mangaBaka: mangaBaka,
            kitsu: new KitsuTracker(
                noopHttp, appSettings, userSettings, tokens, options, NullLogger<KitsuTracker>.Instance),
            logger: NullLogger<ScrobbleService>.Instance);
    }

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

    /// <summary>
    /// A read unnumbered chapter (a prologue, an extra) is all an ongoing series may have downloaded,
    /// and neither Maki nor the tracker knows its total while it runs. Only a finished work is
    /// completed on that evidence; completing the ongoing one would stick, since a completed entry is
    /// never demoted.
    /// </summary>
    [Fact]
    public async Task NativePassAsync_CompletesAFinishedOneShotButNotAnOngoingSeriesWithUnknownTotals()
    {
        using var db = new TestDb();
        var user = db.SeedUser("reader", MakiPermission.None);
        var ongoing = db.SeedSeries("Ongoing", configure: s =>
        {
            s.MangaBakaId = 500;
            s.Status = SeriesStatus.Ongoing;
            s.TotalChapters = null;
        });
        var oneShot = db.SeedSeries("One-shot", configure: s =>
        {
            s.MangaBakaId = 600;
            s.Status = SeriesStatus.Completed;
            s.TotalChapters = null;
        });
        SeedReadSpecial(db, user, ongoing);
        SeedReadSpecial(db, user, oneShot);

        var service = BuildService(db, new FakeUserSettingsStore(db));
        var tracker = new FakeScrobbleTracker();

        await service.NativePassAsync(user, [tracker], ownsKavita: false, CancellationToken.None);

        Assert.Contains(tracker.Pushes, p => p.RemoteId == "600" && p.Chapter == 1 && p.Status == ScrobbleStatus.Completed);
        Assert.DoesNotContain(tracker.Pushes, p => p.RemoteId == "500");
    }

    /// <summary>
    /// A series whose status is unknown and whose total is unknown has no positive evidence of being
    /// over (Kitsu and MangaBaka never say whether it is releasing). It must not be completed; the
    /// same series is completed once the tracker confirms it is not releasing.
    /// </summary>
    [Fact]
    public async Task NativePassAsync_DoesNotCompleteAnUnknownStatusSeriesWithoutTrackerEvidence()
    {
        using var db = new TestDb();
        var user = db.SeedUser("reader", MakiPermission.None);
        var unknown = db.SeedSeries("Unknown", configure: s =>
        {
            s.MangaBakaId = 700;
            s.Status = SeriesStatus.Unknown;
            s.TotalChapters = null;
        });
        SeedReadSpecial(db, user, unknown);

        var service = BuildService(db, new FakeUserSettingsStore(db));
        var silent = new FakeScrobbleTracker();
        await service.NativePassAsync(user, [silent], ownsKavita: false, CancellationToken.None);
        Assert.DoesNotContain(silent.Pushes, p => p.RemoteId == "700");

        var confirming = new FakeScrobbleTracker { Entry = new RemoteEntry(Releasing: false) };
        await service.NativePassAsync(user, [confirming], ownsKavita: false, CancellationToken.None);
        Assert.Contains(confirming.Pushes, p => p.RemoteId == "700" && p.Chapter == 1 && p.Status == ScrobbleStatus.Completed);
    }

    /// <summary>
    /// Only an unnumbered special was read on a work the tracker lists with 20 chapters: the one-shot
    /// rule must not lift the mark to chapter 1 and push Reading.
    /// </summary>
    [Fact]
    public async Task NativePassAsync_ASpecialOnlyReadOfAMultiChapterSeriesPushesNothing()
    {
        using var db = new TestDb();
        var user = db.SeedUser("reader", MakiPermission.None);
        var series = db.SeedSeries("Twenty", configure: s =>
        {
            s.MangaBakaId = 800;
            s.Status = SeriesStatus.Completed;
            s.TotalChapters = null;
        });
        SeedReadSpecial(db, user, series);

        var service = BuildService(db, new FakeUserSettingsStore(db));
        var tracker = new FakeScrobbleTracker
        {
            Entry = new RemoteEntry(Status: ScrobbleStatus.PlanToRead, TotalChapters: 20),
        };

        await service.NativePassAsync(user, [tracker], ownsKavita: false, CancellationToken.None);

        Assert.DoesNotContain(tracker.Pushes, p => p.RemoteId == "800");
    }

    private static void SeedReadSpecial(TestDb db, int userId, int seriesId)
    {
        using var seed = db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId, RelativePath = $"{seriesId}/special.cbz", SourceName = "import",
            DateAdded = DateTime.UtcNow,
        };
        seed.ChapterFiles.Add(file);
        seed.SaveChanges();
        var chapter = new Chapter
        {
            SeriesId = seriesId, Number = null, IsOneShot = true, Title = "Prologue", Language = "en",
            ChapterFileId = file.Id,
        };
        seed.Chapters.Add(chapter);
        seed.SaveChanges();
        seed.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId, SeriesId = seriesId, ChapterId = chapter.Id, PageIndex = 19, PageCount = 20,
            Completed = true, StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
    }

    /// <summary>Covers #101: two library series whose titles normalize to the same key ("Overlord" /
    /// "OVERLORD") must not let either one's cross-ids be attributed to a Kavita series under that
    /// name - the ambiguous key is dropped from the index entirely rather than keeping whichever
    /// series was seen first.</summary>
    [Fact]
    public async Task BuildLibraryIndexAsync_CollidingNormalizedTitles_AreExcluded()
    {
        using var db = new TestDb();
        var user = db.SeedUser("reader", MakiPermission.None);
        db.SeedSeries("Overlord", configure: s => s.AniListId = 111);
        db.SeedSeries("OVERLORD", configure: s => s.AniListId = 222);
        db.SeedSeries("Unique Series", configure: s => s.AniListId = 333);

        var service = BuildService(db, new FakeUserSettingsStore(db));
        var index = await service.BuildLibraryIndexAsync(user, allRootFolders: true, CancellationToken.None);

        Assert.False(index.ContainsKey("overlord"));
        Assert.True(index.ContainsKey("unique series"));
        Assert.Equal(333, index["unique series"].AniListId);
    }

    /// <summary>
    /// Covers #103: <c>ResolveAsync</c> used to merge saved mappings into the derivation dictionary
    /// with <c>TryAdd</c> after library cross-ids, so a stale library id (from an earlier bad match)
    /// silently won over a user's saved correction whenever both named the same service. The fix
    /// assigns saved mappings first, with the indexer, so they are never displaced.
    /// </summary>
    [Fact]
    public void MergeIdsForDerivation_SavedMappingWinsOverStaleLibraryId()
    {
        var savedMappings = new Dictionary<string, string> { ["anilist"] = "123" }; // the user's correction
        var libraryIds = new Dictionary<string, string> { ["anilist"] = "999", ["mal"] = "50" }; // stale
        var webLinkIds = new Dictionary<string, string>();

        var merged = ScrobbleService.MergeIdsForDerivation(savedMappings, libraryIds, webLinkIds);

        Assert.Equal("123", merged["anilist"]); // saved mapping wins the conflict
        Assert.Equal("50", merged["mal"]); // library still fills a service with no saved mapping
    }

    /// <summary>
    /// Covers #99: the rating-import preview builder used to append directly to the list the
    /// controller reads, so a concurrent poll during a slow preview could enumerate mid-<c>Add</c> and
    /// throw. The fix builds into a private list and republishes an immutable snapshot after each add.
    /// Exercises the real <c>QueueRatingImportPreview</c>/<c>GetRatingImport</c> path end to end, with
    /// a fake HTTP handler standing in for MangaBaka so each series lookup is slow enough to race the
    /// concurrent poller below.
    /// </summary>
    [Fact]
    public async Task RatingImportPreview_ConcurrentPollDuringSlowPreview_DoesNotThrow()
    {
        using var db = new TestDb();
        var user = db.SeedUser("reader", MakiPermission.None);
        // The preview paces itself 1.2s between series (Pace), so a handful is plenty of time for the
        // poller below to race the builder without the test running for tens of seconds.
        for (var i = 0; i < 3; i++)
        {
            db.SeedSeries($"Series {i}", configure: s => s.MangaBakaId = 1000 + i);
        }

        var userSettings = new FakeUserSettingsStore(db);
        await userSettings.SetAsync(user, SettingKeys.ScrobbleMangaBakaToken, "test-pat");

        var handler = new FakeHttpMessageHandler(async (request, ct) =>
        {
            await Task.Delay(15, ct); // slow enough for the poller below to race the builder
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v2/series/", StringComparison.Ordinal))
            {
                return JsonResponse("""{"data":{"titles_romaji":["Test"]}}""");
            }

            if (path.StartsWith("/v1/my/library/", StringComparison.Ordinal))
            {
                var remoteId = int.Parse(path.Split('/')[^1]);
                var rating = remoteId % 10 + 1;
                return JsonResponse(
                    "{\"status\":200,\"data\":{\"rating\":" + rating.ToString(CultureInfo.InvariantCulture) + "}}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var mangaBaka = new MangaBakaTracker(
            new FakeHttpClientFactory(new HttpClient(handler)), userSettings, new FakeScrobbleTokenStore(),
            new ScrobbleTrackerOptions(), NullLogger<MangaBakaTracker>.Instance);
        var service = BuildService(db, userSettings, mangaBaka);

        service.QueueRatingImportPreview(user, allRootFolders: true, "mangabaka");

        var pollFailures = new List<Exception>();
        var pollCts = new CancellationTokenSource();
        var poller = Task.Run(() =>
        {
            while (!pollCts.IsCancellationRequested)
            {
                try
                {
                    // Enumerating while the background task republishes the reference is exactly what
                    // ApplyRatingImportAsync/GetRatingImport do; a torn read here is the bug.
                    foreach (var item in service.GetRatingImport(user, "mangabaka").Items)
                    {
                        _ = item.SeriesId;
                    }
                }
                catch (Exception e)
                {
                    pollFailures.Add(e);
                }
            }
        });

        while (service.GetRatingImport(user, "mangabaka").Running)
        {
            await Task.Delay(5);
        }

        pollCts.Cancel();
        await poller;

        Assert.Empty(pollFailures);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeScrobbleTokenStore : IScrobbleTokenStore
    {
        public Task<ScrobbleToken?> GetAsync(int userId, string service, CancellationToken ct = default) =>
            Task.FromResult<ScrobbleToken?>(null);

        public Task SaveAsync(ScrobbleToken token, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(int userId, string service, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAppSettings : IAppSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Covers #102: <c>MatchByTitleAsync</c> used to search the alternate title only when the primary
    /// search returned zero candidates - a primary search that came back with unrelated results never
    /// got a second chance, even though the alt title's own search would have found a confident match.
    /// The fix also tries the alt title when no primary candidate passes <c>BestCandidate</c>, and
    /// scores the combined set.
    /// </summary>
    [Fact]
    public async Task MatchByTitleAsync_TriesAltTitle_WhenPrimaryCandidatesAllFailBestCandidate()
    {
        using var db = new TestDb();
        var user = db.SeedUser("reader", MakiPermission.None);

        var tracker = new ConfigurableScrobbleTracker
        {
            ResultsByQuery = new Dictionary<string, IReadOnlyList<ScrobbleCandidate>>
            {
                // Primary title returns candidates, but nothing close enough to auto-accept.
                ["Frieren: Beyond Journey's End"] =
                    [new ScrobbleCandidate("1", "Some Unrelated Manga", [], "")],
                // The alt (native/original) title finds the real series.
                ["Sousou no Frieren"] =
                    [new ScrobbleCandidate("9", "Sousou no Frieren", [], "")],
            },
        };

        var service = BuildService(db, new FakeUserSettingsStore(db));

        var remoteId = await service.MatchByTitleAsync(
            user, kavitaSeriesId: 1, "Frieren: Beyond Journey's End", "Sousou no Frieren", tracker,
            CancellationToken.None);

        Assert.Equal("9", remoteId);
    }

    private sealed class ConfigurableScrobbleTracker : IScrobbleTracker
    {
        public required Dictionary<string, IReadOnlyList<ScrobbleCandidate>> ResultsByQuery { get; init; }

        public string Name => "anilist";
        public string Label => "AniList";
        public bool UsesOAuth => true;

        public Task<bool> ConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> UsernameAsync(int userId, CancellationToken ct = default) =>
            Task.FromResult<string?>("fake");

        public Task<RemoteEntry> GetEntryAsync(int userId, string remoteId, CancellationToken ct = default) =>
            Task.FromResult(new RemoteEntry());

        public Task UpdateAsync(
            int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateRatingAsync(int userId, string remoteId, int score, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(
            int userId, string title, CancellationToken ct = default) =>
            Task.FromResult(ResultsByQuery.GetValueOrDefault(title, []));

        public string EntryUrl(string remoteId) => $"https://example.test/{remoteId}";
    }

    private sealed class FakeScrobbleTracker : IScrobbleTracker
    {
        public List<(string RemoteId, int Chapter, int Volume, ScrobbleStatus Status)> Pushes { get; } = [];
        public RemoteEntry Entry { get; init; } = new();

        public string Name => "mangabaka";
        public string Label => "MangaBaka";
        public bool UsesOAuth => false;

        public Task<bool> ConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> UsernameAsync(int userId, CancellationToken ct = default) =>
            Task.FromResult<string?>("fake");

        public Task<RemoteEntry> GetEntryAsync(int userId, string remoteId, CancellationToken ct = default) =>
            Task.FromResult(Entry);

        public Task UpdateAsync(
            int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
            CancellationToken ct = default)
        {
            Pushes.Add((remoteId, chapter, volume, status));
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
