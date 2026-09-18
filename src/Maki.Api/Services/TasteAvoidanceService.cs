using System.Security.Cryptography;
using System.Text;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>
/// One thing the reader's avoided titles have in common, and the titles that say so.
/// </summary>
/// <param name="Share">
/// How much of the avoided set carries this facet, in (0, 1]. Sent so the client can word the chip
/// without re-deriving it from a total it would have to be told separately.
/// </param>
public record AvoidanceLabel(
    string Label, string Kind, int Support, double Share, IReadOnlyList<AvoidanceExample> Examples);

public record AvoidanceExample(long MangaBakaId, string Title);

/// <summary>
/// What the titles a reader pushed down have in common, for the Lab to say out loud.
///
/// <para>
/// <strong>This is display only and must stay that way.</strong> Feeding a label back into the
/// ranking would be a coarse mechanism doing a job the vectors already do better: "you avoid Harem"
/// is a sentence about four books, and acting on it would demote several thousand. Note that with
/// <c>EmbeddingMath.Weights.Avoid</c> at 0 the ranking does not act on the avoided set either - what
/// ships is that those titles stop being positive seeds - so nothing rendered from this may claim
/// that similar titles rank lower.
/// </para>
///
/// <para>
/// The cuts are <see cref="TasteGroupMining"/>'s, reused rather than copied, and they exist for the
/// same reason there: without a specificity floor the honest answer for almost any set of titles is
/// "School" or "Japan", which are backdrops nobody has an opinion about. The support and share
/// thresholds are what stop one thumbs down claiming a genre.
/// </para>
/// </summary>
public class TasteAvoidanceService(
    MangaBakaLocalStore store,
    VectorIndexCache vectorIndex,
    EmbeddingStore embeddings,
    ILogger<TasteAvoidanceService> logger)
{
    /// <summary>Labels shown. More than a handful stops being a summary.</summary>
    private const int MaxLabels = 5;

    /// <summary>
    /// Avoided titles a facet needs before it may be named.
    ///
    /// <para>
    /// Three, because two is a coincidence a reader will read as a rule. Somebody who thumbs down
    /// one harem comedy has said something about that book; saying "you avoid Harem" back to them
    /// invents an opinion and then appears to have acted on it, which the ranking has not.
    /// </para>
    /// </summary>
    private const int MinSupport = 3;

    /// <summary>
    /// How much of the avoided set a facet has to cover. Half, so a label describes the set rather
    /// than a corner of it: a reader with ten dislikes and three harems among them has not told
    /// anybody they avoid harem.
    /// </summary>
    private const double MinShare = 0.5;

    private const int CacheSlots = 40;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (IReadOnlyList<AvoidanceLabel> Labels, DateTime At)> _cache = [];

    /// <param name="avoided">
    /// <c>SeedSnapshot.Avoided</c>, so the visibility rules are already applied: nothing fully
    /// incognito and nothing over the content ceiling reached it, and an ignored source was removed.
    /// A label can therefore never name a title the reader hid from this surface.
    /// </param>
    /// <param name="allowedRatings">
    /// The caller's content ceiling, for the dump read. The ids were already filtered, but the
    /// catalogue read has to agree or the example titles would come back empty for some of them.
    /// </param>
    public async Task<IReadOnlyList<AvoidanceLabel>> LabelsAsync(
        IReadOnlyDictionary<long, double> avoided, IReadOnlyList<string> allowedRatings,
        CancellationToken ct = default)
    {
        if (avoided.Count < MinSupport || !await store.IsAvailableAsync(ct))
        {
            return [];
        }

        var key = Fingerprint(avoided, allowedRatings);
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
            {
                return hit.Labels;
            }

            var labels = await BuildAsync(avoided, allowedRatings, ct);
            _cache[key] = (labels, DateTime.UtcNow);
            foreach (var stale in _cache
                         .Where(kv => DateTime.UtcNow - kv.Value.At >= CacheFor)
                         .Select(kv => kv.Key).ToList())
            {
                _cache.Remove(stale);
            }

            while (_cache.Count > CacheSlots)
            {
                _cache.Remove(_cache.MinBy(kv => kv.Value.At).Key);
            }

            return labels;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IReadOnlyList<AvoidanceLabel>> BuildAsync(
        IReadOnlyDictionary<long, double> avoided, IReadOnlyList<string> allowedRatings,
        CancellationToken ct)
    {
        var index = await vectorIndex.GetAsync(ct);
        if (index is null || index.Count == 0)
        {
            return [];
        }

        var entries = await store.GetByIdsAsync(avoided.Keys.ToList(), allowedRatings, ct);
        var titles = entries
            .Where(x => long.TryParse(x.ProviderId, out _))
            .ToDictionary(x => long.Parse(x.ProviderId), x => x);
        if (titles.Count < MinSupport)
        {
            return [];
        }

        var vocab = embeddings.GetVocab();
        var genreIds = TasteInsightsService.GenreIdsOf(
            index, titles.Values.SelectMany(x => x.MatchedGenres));
        var genreDf = TasteInsightsService.GenreDocumentFrequencies(index, genreIds);

        // Facet key -> the avoided titles carrying it. Tags are keyed "#name" and genres "@name",
        // the same way the group mining keys them, because the two vocabularies overlap and a name
        // one of them already claimed must not be counted twice.
        var members = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var names = new Dictionary<string, (string Name, bool IsTag, long Df)>(StringComparer.Ordinal);

        foreach (var id in titles.Keys)
        {
            if (!index.TryGetRow(id, out var row))
            {
                continue;
            }

            foreach (var (tagId, cls) in TagMath.Unpack(index.TagsAt(row)))
            {
                if (cls < TasteInsightsService.MinTagClass ||
                    !vocab.TryGetValue(tagId, out var info) ||
                    info.IsSpoiler ||
                    !TasteInsightsService.GroupCategories.Contains(info.Category) ||
                    string.IsNullOrWhiteSpace(info.Name))
                {
                    continue;
                }

                Record("#" + info.Name.ToLowerInvariant(), info.Name, true, info.SeriesCount, id);
            }
        }

        // Genres second and only under a name no tag claimed, for the reason the mining records:
        // "School Life" is both, and keying them separately once mined a pair naming it twice.
        var tagNames = names.Values.Where(v => v.IsTag).Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in titles)
        {
            foreach (var genre in entry.MatchedGenres)
            {
                var trimmed = genre.Trim();
                if (trimmed.Length == 0 ||
                    tagNames.Contains(trimmed) ||
                    TasteInsightsService.DemographicGenres.Contains(trimmed) ||
                    !genreIds.TryGetValue(trimmed, out var genreId) ||
                    !genreDf.TryGetValue(genreId, out var df))
                {
                    continue;
                }

                Record("@" + trimmed.ToLowerInvariant(), trimmed, false, df, id);
            }
        }

        var corpus = Math.Max(index.Count, names.Values.Select(v => v.Df).DefaultIfEmpty(0).Max() + 1);
        var ranked = Select(
            [.. members.Select(kv => new Candidate(
                names[kv.Key].Name, names[kv.Key].IsTag, names[kv.Key].Df, [.. kv.Value.Distinct()]))],
            titles.ToDictionary(kv => kv.Key, kv => kv.Value.Title),
            corpus);

        logger.LogDebug(
            "Avoidance labels: {Labels} of {Facets} facets over {Titles} avoided titles",
            ranked.Count, members.Count, titles.Count);
        return ranked;

        void Record(string key, string name, bool isTag, long df, long id)
        {
            names[key] = names.TryGetValue(key, out var seen)
                // Casing variants are separate vocabulary ids with separate counts; the largest is
                // the one that saw the whole catalogue.
                ? seen with { Df = Math.Max(seen.Df, df) }
                : (name, isTag, df);
            if (!members.TryGetValue(key, out var list))
            {
                members[key] = list = [];
            }

            list.Add(id);
        }
    }

    /// <summary>
    /// One facet of the avoided set, before the thresholds decide whether it may be named.
    /// </summary>
    /// <param name="Df">How many catalogue rows carry it, which is what its specificity is read off.</param>
    internal sealed record Candidate(string Name, bool IsTag, long Df, IReadOnlyList<long> Carriers);

    /// <summary>
    /// Which facets earn a chip, and in what order. Separated from the index and dump reads above so
    /// the three thresholds can be tested on synthetic facet sets, which is how
    /// <see cref="TasteGroupMining"/> is tested and for the same reason: every question here is
    /// about the arithmetic, not about where the tags came from.
    /// </summary>
    internal static IReadOnlyList<AvoidanceLabel> Select(
        IReadOnlyList<Candidate> candidates, IReadOnlyDictionary<long, string> titles, long corpus)
    {
        if (titles.Count == 0)
        {
            return [];
        }

        var qualifying = new List<(AvoidanceLabel Label, double Specificity)>();
        foreach (var candidate in candidates)
        {
            var support = candidate.Carriers.Distinct().Count();
            var share = (double)support / titles.Count;
            var specificity = Math.Min(
                Math.Log((double)corpus / Math.Clamp(candidate.Df, 1, Math.Max(1, corpus - 1))),
                TasteGroupMining.MaxSpecificity);
            if (support < MinSupport ||
                share < MinShare ||
                specificity < TasteGroupMining.MinSingleSpecificity)
            {
                continue;
            }

            qualifying.Add((
                new AvoidanceLabel(
                    candidate.Name,
                    candidate.IsTag ? "tag" : "genre",
                    support,
                    share,
                    candidate.Carriers.Distinct().Order()
                        .Where(titles.ContainsKey)
                        .Select(id => new AvoidanceExample(id, titles[id]))
                        .ToList()),
                specificity));
        }

        // Support first, because evidence is the question a reader would ask of a claim about their
        // own shelf; specificity only breaks ties between facets carried by the same titles.
        return qualifying
            .OrderByDescending(x => x.Label.Support)
            .ThenByDescending(x => x.Specificity)
            .ThenBy(x => x.Label.Label, StringComparer.Ordinal)
            .Take(MaxLabels)
            .Select(x => x.Label)
            .ToList();
    }

    private static string Fingerprint(
        IReadOnlyDictionary<long, double> avoided, IReadOnlyList<string> allowedRatings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{string.Join(',', avoided.Keys.Order())}|{string.Join(',', allowedRatings)}")))[..32];
}
