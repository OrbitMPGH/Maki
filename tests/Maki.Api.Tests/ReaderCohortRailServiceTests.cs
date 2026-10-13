using Maki.Api.Services;
using Maki.Core.Recommendations;
using Maki.Core.Security;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The cohort rail takes its slots before hydration. A candidate list that leads with titles above
/// the reader's ceiling must not leave them with a short rail, so the ceiling has to be part of the
/// predicate that decides who takes a slot.
/// </summary>
public sealed class ReaderCohortRailServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DumpDbBuilder _dump = new();
    private readonly string _vectorDir = Path.Combine(Path.GetTempPath(), "maki-cohort-rail-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _dump.Dispose();
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_vectorDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Ranks adult titles first and honours the predicate the rail hands it, like the real service.</summary>
    private sealed class FixedCandidates(params long[] ids) : ReaderCohortService(
        null!, null!, null!, null!, null!, NullLogger<ReaderCohortService>.Instance)
    {
        public override Task<IReadOnlyList<long>> GetCandidatesAsync(
            ICurrentUser scope, IReadOnlySet<long> owned, Func<long, bool>? accept, int limit,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<long>>(
                ids.Where(id => !owned.Contains(id) && (accept?.Invoke(id) ?? true)).Take(limit).ToList());
    }

    private ReaderCohortRailService Rail()
    {
        _dump.AddSeries(11, "Adult One", contentRating: "pornographic", rating: 80)
            .AddSeries(12, "Adult Two", contentRating: "pornographic", rating: 80)
            .AddSeries(13, "Fine One", contentRating: "suggestive", rating: 80)
            .AddSeries(14, "Fine Two", contentRating: "safe", rating: 80);
        var options = new MangaBakaDumpOptions(_dump.Path, Path.GetTempPath());
        Directory.CreateDirectory(_vectorDir);
        var embedding = new EmbeddingOptions(_vectorDir, Path.Combine(_vectorDir, "embeddings.db"), _vectorDir,
            EmbeddingModelProfile.Base with { Dimensions = 4 });
        var vectors = new EmbeddingStore(embedding);
        vectors.EnsureSchema();
        vectors.UpsertBatch(
        [
            (11L, "h", [1f, 0f, 0f, 0f]), (12L, "h", [0f, 1f, 0f, 0f]),
            (13L, "h", [0f, 0f, 1f, 0f]), (14L, "h", [0f, 0f, 0f, 1f]),
        ]);

        var settings = new FakeAppSettings();
        return new ReaderCohortRailService(
            _db.ScopeFactory(),
            new SeedWeightService(new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            new FixedCandidates(11, 12, 13, 14),
            new VectorIndexCache(embedding, options, NullLogger<VectorIndexCache>.Instance),
            new MangaBakaLocalStore(options, settings, NullLogger<MangaBakaLocalStore>.Instance),
            NullLogger<ReaderCohortRailService>.Instance);
    }

    [Fact]
    public async Task A_restricted_reader_gets_a_full_rail_when_the_best_ranked_titles_are_over_their_ceiling()
    {
        _db.SeedSeries("Owned", configure: s => s.MangaBakaId = 99);

        var rail = await Rail().GetAsync(new TestCurrentUser(1), filters: null, limit: 2);

        Assert.NotNull(rail);
        Assert.Equal(["13", "14"], rail.Items.Select(i => i.ProviderId).Order());
    }
}
