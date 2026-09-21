using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// One slow or unreachable MAL relation lookup must not fail the whole pass, or the 500 the reader
/// sees on "Sync now" for a 30-second HttpClient timeout on a single anime. <see cref="MalTracker"/>
/// is the only <see cref="IAnimeListSource"/> whose relation lookup is per-anime - AniList's list call
/// carries relations already - so the fake here stands in for that one slow request.
/// </summary>
public class AnimeSignalSyncTests : IDisposable
{
    private readonly TestDb _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private void OptIn(int userId)
    {
        using var db = _fixture.NewContext();
        db.UserSettings.Add(new UserSetting
        {
            UserId = userId,
            Key = SettingKeys.RecommendationsAnimeSignalsEnabled,
            Value = "true",
        });
        db.SaveChanges();
    }

    private AnimeSignalSyncService Service(FakeAnimeListSource source) => new(
        _fixture.ScopeFactory(),
        new FakeAnimeSignalSources(source),
        new MangaBakaLocalStore(
            new MangaBakaDumpOptions("", Path.GetTempPath()), new FakeAppSettings(),
            NullLogger<MangaBakaLocalStore>.Instance),
        new FakeAppSettings(),
        new FakeUserSettingsStore(_fixture),
        NullLogger<AnimeSignalSyncService>.Instance);

    [Fact]
    public async Task A_timed_out_relation_lookup_does_not_fail_the_pass()
    {
        var userId = _fixture.SeedUser();
        OptIn(userId);

        var source = new FakeAnimeListSource(
            [
                new AnimeListEntry(1, "Times out", 8, AnimeWatchStatus.Completed),
                new AnimeListEntry(2, "Resolves fine", 9, AnimeWatchStatus.Completed),
                new AnimeListEntry(3, "Also fine", 7, AnimeWatchStatus.Completed),
            ],
            failing: 1);

        var summary = await Service(source).SyncUserAsync(userId, CancellationToken.None);

        Assert.Equal(3, summary.Fetched);
        Assert.Equal(1, summary.LookupFailures);
        // The two that resolved cleanly, not the one that timed out.
        Assert.Equal(2, summary.Looked);

        using var db = _fixture.NewContext();
        var rows = await db.AnimeSignals.Where(x => x.UserId == userId).ToDictionaryAsync(x => x.AnimeId);
        Assert.Null(rows[1].MatchAttemptedAtUtc);
        Assert.NotNull(rows[2].MatchAttemptedAtUtc);
        Assert.NotNull(rows[3].MatchAttemptedAtUtc);
    }

    /// <summary>A real cancellation must still propagate rather than being swallowed as a lookup failure.</summary>
    [Fact]
    public async Task A_real_cancellation_still_propagates()
    {
        var userId = _fixture.SeedUser();
        OptIn(userId);

        using var cts = new CancellationTokenSource();
        var source = new FakeAnimeListSource(
            [new AnimeListEntry(1, "Cancelled mid-lookup", 8, AnimeWatchStatus.Completed)],
            failing: 1, cancelWith: cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(source).SyncUserAsync(userId, cts.Token));
    }

    private sealed class FakeAnimeSignalSources(FakeAnimeListSource source) : AnimeSignalSources(null!, null!)
    {
        public override Task<IReadOnlyList<IAnimeListSource>> EnabledAsync(
            int userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAnimeListSource>>([source]);
    }

    /// <summary>
    /// Implements <see cref="IScrobbleTracker"/> only far enough for <c>AnimeSignalSources.NameOf</c>
    /// to read <see cref="Name"/> - nothing else here exercises scrobbling.
    /// </summary>
    private sealed class FakeAnimeListSource(
        IReadOnlyList<AnimeListEntry> entries, long failing, CancellationTokenSource? cancelWith = null)
        : IAnimeListSource, IScrobbleTracker
    {
        public string Name => "mal";
        public string Label => "MyAnimeList";
        public bool UsesOAuth => true;

        public Task<IReadOnlyList<AnimeListEntry>> ListAnimeAsync(int userId, CancellationToken ct = default) =>
            Task.FromResult(entries);

        public Task<AnimeRelatedManga?> RelatedMangaAsync(int userId, long animeId, CancellationToken ct = default)
        {
            if (animeId == failing)
            {
                if (cancelWith is { } cts)
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                // What HttpClient's own request timeout surfaces as: a TaskCanceledException whose
                // token is not the caller's.
                throw new TaskCanceledException("simulated MAL timeout");
            }

            return Task.FromResult<AnimeRelatedManga?>(new AnimeRelatedManga(null, 999));
        }

        public Task<bool> ConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> UsernameAsync(int userId, CancellationToken ct = default) =>
            Task.FromResult<string?>("reader");
        public Task<RemoteEntry> GetEntryAsync(int userId, string remoteId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateAsync(
            int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateRatingAsync(int userId, string remoteId, int score, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(
            int userId, string title, CancellationToken ct = default) => throw new NotSupportedException();
        public string EntryUrl(string remoteId) => throw new NotSupportedException();
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
