using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.ReaderCohorts;

namespace Maki.Api.Services;

/// <summary>
/// What readers with this reader's habits made of a series, when that differs from what readers
/// made of it in general.
///
/// <para>
/// <b>Placed by what they have READ, never by what they own.</b> Somebody with two hundred unread
/// action titles on the shelf and forty finished romcoms is a romcom reader, and the same
/// distinction the taste page draws as <see cref="TasteView.Read"/> against
/// <see cref="TasteView.Shelf"/> decides it here. The read population comes from
/// <see cref="BehavioralTasteService.ReadSignalsAsync"/> so the two surfaces cannot drift on
/// incognito or root-folder visibility.
/// </para>
///
/// <para>
/// <b>Strictly personal, like the taste page.</b> No user parameter anywhere on this path: "readers
/// like you rated this higher" is not a fact about anybody but the caller.
/// </para>
/// </summary>
public class ReaderCohortService(
    IServiceScopeFactory scopeFactory,
    SeedWeightService seedWeights,
    BehavioralTasteService taste,
    ReaderCohortCache cohorts,
    ReaderCohortTuning tuning,
    IAppSettings settings,
    ILogger<ReaderCohortService> logger)
{
    /// <summary>
    /// Same thirty minutes the taste profile uses, and for the same reason: placement is a fact
    /// about what somebody has been reading, and one that ignores this afternoon reads as broken.
    /// The work behind a miss is one library query, not an index scan.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private const int CacheSlots = 40;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<int, (IReadOnlyDictionary<int, double> Weights, int ReadCount, DateTime At)>
        _placements = [];

    /// <summary>
    /// The hint for one series, or null when there is nothing worth saying: the artifact is absent
    /// or switched off, the reader cannot be placed, too few of their cohorts' readers scored it, or
    /// the cohorts agree with everybody else closely enough that a reader could not see the
    /// difference.
    /// </summary>
    public async Task<ReaderCohortHint?> GetHintAsync(
        ICurrentUser scope, long mangaBakaId, CancellationToken ct = default)
    {
        if (!await EnabledAsync(ct))
        {
            return null;
        }

        var index = await cohorts.GetAsync(ct);
        if (index is null || !index.TryGetSlot(mangaBakaId, out var slot))
        {
            return null;
        }

        // The comparison is against the SAME population, not against the catalogue rating shown
        // beside it. MangaBaka's aggregate averages several metadata providers; these readers are
        // one crowd on one site. A gap between those two says something about the two sources, not
        // about this reader, and would fire the hint constantly for the wrong reason.
        if (index.GlobalMeanAt(slot) is not { } baseline)
        {
            return null;
        }

        var weights = await PlaceAsync(scope, index, ct);
        if (weights.Count == 0)
        {
            return null;
        }

        double numerator = 0, denominator = 0;
        var raters = 0;
        foreach (var entry in index.EntriesAt(slot))
        {
            if (entry.Mean is not { } mean || entry.Raters <= 0
                || !weights.TryGetValue(entry.Cohort, out var weight))
            {
                continue;
            }

            // Weighted by how many of that cohort actually rated it as well as by how well the
            // reader matches it, so a cohort that barely scored the series cannot outvote one that
            // did.
            numerator += weight * entry.Raters * mean;
            denominator += weight * entry.Raters;
            raters += entry.Raters;
        }

        if (denominator <= 0 || raters < tuning.MinRaters)
        {
            return null;
        }

        var score = numerator / denominator;
        return Math.Abs(score - baseline) < tuning.MinDivergence
            ? null
            : new ReaderCohortHint(Math.Round(score, 1), Math.Round(baseline, 1), raters);
    }

    /// <summary>
    /// What the reader's own cohorts finished that this reader has not, best first. Null when the
    /// artifact is absent or switched off and empty when the reader cannot be placed; both are
    /// ordinary states rather than errors.
    /// </summary>
    /// <param name="owned">
    /// Series to leave out, as MangaBaka ids. The caller's whole library, not just what they read:
    /// a rail that recommends something already on the shelf is noise whether or not it was opened.
    /// </param>
    /// <param name="accept">
    /// Applied per candidate before it takes a slot, so filters narrow the ranking rather than
    /// deleting rows from an already-cut page. Null accepts everything.
    /// </param>
    public async Task<IReadOnlyList<long>> GetCandidatesAsync(
        ICurrentUser scope, IReadOnlySet<long> owned, Func<long, bool>? accept, int limit,
        CancellationToken ct = default)
    {
        if (!await EnabledAsync(ct))
        {
            return [];
        }

        var index = await cohorts.GetAsync(ct);
        if (index is null)
        {
            return [];
        }

        var weights = await PlaceAsync(scope, index, ct);
        if (weights.Count == 0)
        {
            return [];
        }

        return Rank(
            index, weights, owned, accept, Math.Min(limit, tuning.MaxCandidates),
            tuning.PopularityDamping);
    }

    /// <summary>
    /// Ranks what the matched cohorts finished, by completion rate with the series' overall rate
    /// divided back out to the power <paramref name="damping"/>.
    /// <para>
    /// The division is the whole anti-popularity mechanism. A title most people finish has a high
    /// rate in <em>every</em> cohort, so without it the rail returns the same famous list to
    /// everybody. Taken all the way it overshoots into titles almost nobody finishes, which is why
    /// this is an exponent rather than a switch.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<long> Rank(
        ReaderCohortIndex index,
        IReadOnlyDictionary<int, double> weights,
        IReadOnlySet<long> owned,
        Func<long, bool>? accept,
        int limit,
        double damping)
    {
        var scored = new Dictionary<long, double>(4096);

        // One pass over the whole structure rather than a lookup per candidate per cohort: the
        // index is item-major, so this walks it in slot order and touches each row once.
        index.ForEachEntry((slot, entry) =>
        {
            if (!weights.TryGetValue(entry.Cohort, out var weight) || entry.Completions <= 0)
            {
                return;
            }

            var id = index.IdAt(slot);
            if (owned.Contains(id))
            {
                return;
            }

            var globalRate = index.GlobalRateAt(slot);
            if (globalRate <= 0)
            {
                // No all-readers row means no denominator, and a lift with no denominator is just
                // the cohort's own popularity again.
                return;
            }

            var readers = index.CohortReaders[entry.Cohort];
            if (readers <= 0)
            {
                return;
            }

            var rate = entry.Completions / (double)readers;
            var value = damping <= 0 ? rate : rate / Math.Pow(globalRate, damping);
            scored[id] = scored.GetValueOrDefault(id) + (weight * value);
        });

        return
        [
            .. scored
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key)
                .Select(kv => kv.Key)
                .Where(id => accept?.Invoke(id) ?? true)
                .Take(limit),
        ];
    }

    private async Task<bool> EnabledAsync(CancellationToken ct) =>
        !string.Equals(
            await settings.GetAsync(SettingKeys.RecommendationsReaderCohorts, ct),
            "false",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where this reader sits against the shipped cohorts, from their own finished series alone.
    /// The whole feature turns on this being computable from group aggregates: nothing read here
    /// describes a person other than the caller, and nothing about the caller leaves the instance.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, double>> PlaceAsync(
        ICurrentUser scope, ReaderCohortIndex index, CancellationToken ct)
    {
        var readIds = await ReadPopulationAsync(scope, ct);

        await _lock.WaitAsync(ct);
        try
        {
            // Keyed on the size of the read set as well as on age, so finishing something places
            // again rather than waiting out the half hour.
            if (_placements.TryGetValue(scope.UserId, out var cached)
                && cached.ReadCount == readIds.Count
                && DateTime.UtcNow - cached.At < CacheFor)
            {
                return cached.Weights;
            }

            var weights = Place(index, readIds, tuning.TopCohorts);
            _placements[scope.UserId] = (weights, readIds.Count, DateTime.UtcNow);

            while (_placements.Count > CacheSlots)
            {
                _placements.Remove(_placements.MinBy(kv => kv.Value.At).Key);
            }

            logger.LogDebug(
                "Placed user {User} across {Cohorts} cohorts from {Read} finished series",
                scope.UserId, weights.Count, readIds.Count);
            return weights;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IReadOnlyCollection<long>> ReadPopulationAsync(ICurrentUser scope, CancellationToken ct)
    {
        using var dbScope = scopeFactory.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<MakiDbContext>();
        // A singleton opening its own scope gets an unrestricted DataScope, which would place the
        // reader using root folders they were never granted.
        db.Scope.SetUser(scope.UserId, scope.AllRootFolders);

        var seeded = await seedWeights.BuildAsync(db, scope, ct);
        if (seeded.LibraryIds.Count == 0)
        {
            return [];
        }

        // The raw read set, not the weights: a series whose reading implies a neutral weight was
        // still read, and it still says which cohorts this reader belongs with.
        var signals = await taste.ReadSignalsAsync(db, scope.UserId, seeded.LibraryIds, ct);
        return signals.Keys.ToList();
    }

    /// <summary>
    /// Cohort affinity from the overlap between what the reader finished and what each cohort
    /// finished, weighted so a rare title counts for more than one everybody has read.
    /// <para>
    /// The inverse-frequency term measures as very nearly inert (divergence 11.0% against 11.3%,
    /// MAE 11.674 against 11.678) because the cohort rates are already popularity-normalised by
    /// construction. It is kept because it is the term that expresses the intent — a reader is
    /// defined by what is distinctive about their reading, not by the famous titles everybody
    /// finished — and removing it would leave nothing saying so.
    /// </para>
    /// </summary>
    internal static IReadOnlyDictionary<int, double> Place(
        ReaderCohortIndex index, IReadOnlyCollection<long> readIds, int topCohorts)
    {
        var scores = new double[index.CohortCount];
        var total = Math.Max(1, index.TotalReaders);

        foreach (var id in readIds)
        {
            if (!index.TryGetSlot(id, out var slot))
            {
                continue;
            }

            var completions = index.GlobalCompletionsAt(slot);
            if (completions <= 0)
            {
                continue;
            }

            var idf = Math.Log(Math.Max(1.0, total / (double)(1 + completions)));
            foreach (var entry in index.EntriesAt(slot))
            {
                var readers = index.CohortReaders[entry.Cohort];
                if (readers > 0)
                {
                    scores[entry.Cohort] += idf * (entry.Completions / (double)readers);
                }
            }
        }

        var order = Enumerable.Range(0, scores.Length)
            .Where(c => scores[c] > 0)
            .OrderByDescending(c => scores[c])
            .ThenBy(c => c)
            .Take(topCohorts)
            .ToArray();

        if (order.Length == 0)
        {
            return new Dictionary<int, double>();
        }

        // Subtracting the weakest cohort kept stops the mix flattening into "all of them, equally",
        // which is the all-readers average again under a different name.
        var floor = scores[order[^1]];
        var spread = order.Sum(c => scores[c] - floor);
        var weights = new Dictionary<int, double>(order.Length);
        foreach (var c in order)
        {
            weights[c] = spread > 0 ? (scores[c] - floor) / spread : 1.0 / order.Length;
        }

        return weights;
    }
}
