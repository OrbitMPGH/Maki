namespace Maki.Api.Services;

/// <summary>
/// Finds the recurring, specific things a reader reads, as <em>overlapping</em> sets rather than as
/// a partition of their library.
///
/// <para>
/// This replaced spherical k-means over the embedding vectors, and the reason is what the two can
/// express rather than how well either clusters. A partition has to account for every series once,
/// so with the two to five groups a library supports each one is most of a shelf and its label is
/// whatever separates one half of a library from the other: "Japan + Primarily Teen Cast". Nobody
/// reads "Japan". What they actually have is a dozen smaller habits that share members - a romcom
/// is also a fake-relationship story, a dungeon series is also a monster series - and a partition
/// cannot say that about the same book twice.
/// </para>
///
/// <para>
/// So the unit here is a facet (one tag, or one genre) and a pair of facets, mined the way frequent
/// itemsets are: count singles, keep the ones that recur, pair only those. A group is scored by how
/// much of the library it holds against how rare the facet is in the catalogue, which is what keeps
/// "Fake Relationship" over five titles ahead of "Romance" over forty.
/// </para>
///
/// <para>
/// Free of every Maki type, like the vector math in <see cref="TasteClustering"/>, so the selection
/// can be tested on synthetic facet sets: "does this prefer the specific group" is a question about
/// the arithmetic, not about anybody's library.
/// </para>
/// </summary>
public static class TasteGroupMining
{
    /// <summary>
    /// Below this many series there is nothing to mine: a facet shared by three of six books is not
    /// a habit, it is the library.
    /// </summary>
    public const int MinPoints = 8;

    /// <summary>
    /// Series a facet needs before it is a taste rather than a coincidence. Two books that happen to
    /// share a tag is not a reading habit.
    /// </summary>
    public const int MinMembers = 3;

    /// <summary>Most groups worth showing. Past this the page is a tag cloud.</summary>
    public const int MaxGroups = 12;

    /// <summary>
    /// The largest share of the library a group may hold. A facet on half of what somebody reads
    /// describes the reader, not a group inside them, and its card would be the library again under
    /// a narrower name.
    /// <para>
    /// Set against the simulated library rather than by feel: at two thirds it admitted "School
    /// Life" over 52% of a shelf and "Shounen" over 53%, which are wallpaper. Anything this broad
    /// has a pair underneath it that says something.
    /// </para>
    /// </summary>
    private const double MaxShare = 0.45;

    /// <summary>
    /// How much a facet has to say about a work before it can name a group <em>on its own</em> - an
    /// IDF, so this is roughly "on no more than an eighth of the catalogue".
    ///
    /// <para>
    /// This is the cut that answers the complaint the old partition produced: one broad facet is
    /// never a taste, because a card reading "Romance" over a reader with a hundred romances tells
    /// them something they knew. A conjunction of two broad facets can be - "Romance + Comedy" is
    /// one of the clearest groups a reader can have and neither half of it clears this - so the
    /// floor deliberately applies to singles only, and the pair pass below is what a broad facet is
    /// for.
    /// </para>
    /// </summary>
    private const double MinSingleSpecificity = 2.0;

    /// <summary>
    /// The rarest a facet is allowed to count as, past which extra rarity buys nothing.
    ///
    /// <para>
    /// An IDF is log(1/share) whatever the corpus size, so this is exactly "a tag on half a percent
    /// of the catalogue and a tag on a twentieth of a percent are worth the same". Unclamped, rarity
    /// is unbounded and a hyper-rare descriptive leaf beats a real habit on almost no evidence:
    /// "Comedic Facial Expressions" (164 of ~126k series, four of the simulated shelf) outscored
    /// groups four times its size. Such a tag is usually one annotator's idiosyncrasy rather than a
    /// sharper signal.
    /// </para>
    ///
    /// <para>
    /// The effect below the ceiling is that member count decides between rare facets, which is the
    /// evidence question the raw IDF was answering backwards.
    /// </para>
    /// </summary>
    private static readonly double MaxSpecificity = Math.Log(1 / 0.005);

    /// <summary>
    /// Facets per work that get to form pairs, most specific first. A work can carry thirty tags and
    /// every one of them pairs with every other, so this is what keeps the pair pass from squaring a
    /// long tail that could never clear <see cref="MinMembers"/> anyway.
    /// </summary>
    private const int MaxFacetsPerWork = 12;

    /// <summary>
    /// How much of a pair's second facet counts toward its specificity, at most. Not the full sum:
    /// the two halves of a pair worth naming are correlated by construction (monsters and skills
    /// travel together), so treating them as independent evidence would rank every pair above every
    /// single.
    /// </summary>
    private const double PairMinorWeight = 0.5;

    /// <summary>
    /// How much two groups may share before the second is the first under another name. Set high
    /// because overlap is the point of this shape - a book belongs to several groups - and only a
    /// near-duplicate member set is worth suppressing.
    /// </summary>
    private const double MaxOverlap = 0.6;

    /// <summary>
    /// Whether a pair may name a group when both of its halves already name one.
    ///
    /// <para>
    /// It may not, and the reason is that such a card is arithmetically incapable of saying anything
    /// new. "Love Confession + Student-Student Relationship" is the intersection of two rows already
    /// on the page: its members are a subset of both, and its picks come from the AND of two filters
    /// the reader can already see the results of. A pair earns a card by expressing something
    /// neither half can - which is why "Romance + Comedy" exists, since neither half of it clears
    /// <see cref="MinSingleSpecificity"/> - and once both halves have their own row that is no
    /// longer true. A pair with one selected half still survives: it contributes a name the page
    /// does not otherwise have.
    /// </para>
    /// </summary>
    private const bool PairsNeedAnUnselectedHalf = true;

    /// <summary>
    /// How many groups one facet may appear in.
    ///
    /// <para>
    /// <see cref="MaxOverlap"/> is a test on member sets and cannot see this: measured on the
    /// simulated library, "Student-Student Relationship" named four of twelve groups - once alone
    /// and three times in a pair - and every one of those member sets was different enough to pass.
    /// The sets were honest and the page still read as one word four times. A reader skims labels,
    /// so the repetition is the thing they see, whoever the members are.
    /// </para>
    /// </summary>
    private const int MaxGroupsPerFacet = 2;

    /// <summary>
    /// How many of the best-scoring candidates the selection pass actually walks. Pair mining
    /// produces tens of thousands of them and the pass only ever keeps <see cref="MaxGroups"/>;
    /// anything this far down the ranking would have to survive every rejection test above it.
    /// Deep enough to still fill the page once the facet cap starts turning candidates away, since
    /// a library built on a handful of facets rejects most of its own ranking.
    /// </summary>
    private const int SelectionPool = 800;

    /// <param name="Key">
    /// The caller's id for this facet. Groups come back keyed by it, so the caller owns the mapping
    /// back to a name and to whatever it takes to filter on it.
    /// </param>
    /// <param name="Specificity">
    /// How much carrying this facet says, in the catalogue rather than in the library - an IDF. The
    /// caller supplies it because the document frequencies live in two different places (the tag
    /// vocabulary, the index's genre column) and neither belongs in here.
    /// </param>
    public readonly record struct Facet(int Key, double Specificity);

    /// <param name="Keys">One facet, or two. Two means a work must carry both.</param>
    /// <param name="Members">Indices into the input, ascending.</param>
    public sealed record Group(IReadOnlyList<int> Keys, IReadOnlyList<int> Members, double Score);

    /// <summary>
    /// Mines <paramref name="works"/>, one facet list per series. Returns groups best-scoring first,
    /// or an empty list when there is too little to say.
    /// </summary>
    public static IReadOnlyList<Group> Mine(
        IReadOnlyList<IReadOnlyList<Facet>> works, int maxGroups = MaxGroups)
    {
        if (works.Count < MinPoints)
        {
            return [];
        }

        var specificity = new Dictionary<int, double>();
        var singles = new Dictionary<int, List<int>>();
        for (var i = 0; i < works.Count; i++)
        {
            foreach (var facet in works[i].DistinctBy(f => f.Key))
            {
                specificity[facet.Key] = Math.Min(facet.Specificity, MaxSpecificity);
                if (!singles.TryGetValue(facet.Key, out var members))
                {
                    singles[facet.Key] = members = [];
                }

                members.Add(i);
            }
        }

        // Apriori's one useful property: a pair cannot recur more often than its rarer half, so
        // pairing anything that did not recur on its own is work that can only produce rejects.
        var frequent = singles
            .Where(kv => kv.Value.Count >= MinMembers)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var ceiling = works.Count * MaxShare;
        var candidates = new List<Group>();
        foreach (var (key, members) in frequent)
        {
            if (members.Count > ceiling || specificity[key] < MinSingleSpecificity)
            {
                continue;
            }

            candidates.Add(new Group([key], members, Score(members.Count, specificity[key])));
        }

        foreach (var (pair, members) in Pairs(works, frequent, specificity))
        {
            var (a, b) = pair;
            if (members.Count > ceiling)
            {
                continue;
            }

            candidates.Add(new Group(
                [a, b],
                members,
                Score(members.Count, PairSpecificity(
                    specificity[a], specificity[b],
                    members.Count, Math.Min(frequent[a].Count, frequent[b].Count)))));
        }

        return Select(candidates, maxGroups);
    }

    /// <summary>Members are appended in ascending order of <c>i</c>, which <see cref="Jaccard"/> relies on.</summary>
    private static Dictionary<(int, int), List<int>> Pairs(
        IReadOnlyList<IReadOnlyList<Facet>> works,
        Dictionary<int, List<int>> frequent,
        Dictionary<int, double> specificity)
    {
        var pairs = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < works.Count; i++)
        {
            var keys = works[i]
                .Select(f => f.Key)
                .Distinct()
                .Where(frequent.ContainsKey)
                .OrderByDescending(k => specificity[k])
                .ThenBy(k => k)
                .Take(MaxFacetsPerWork)
                .Order()
                .ToList();

            for (var a = 0; a < keys.Count; a++)
            {
                for (var b = a + 1; b < keys.Count; b++)
                {
                    var key = (keys[a], keys[b]);
                    if (!pairs.TryGetValue(key, out var members))
                    {
                        pairs[key] = members = [];
                    }

                    members.Add(i);
                }
            }
        }

        return pairs
            .Where(kv => kv.Value.Count >= MinMembers)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Greedy, best first, rejecting anything that is a group already shown - by its members
    /// (<see cref="MaxOverlap"/>), by its name (<see cref="MaxGroupsPerFacet"/>) and by being the
    /// intersection of two rows already on the page
    /// (<see cref="PairsNeedAnUnselectedHalf"/>), which are three different ways for the page to
    /// repeat itself. Singles sort ahead of pairs on equal score, so by the time a pair is weighed
    /// its halves have had their chance. Every tie is broken on the keys so the same
    /// library mines to the same groups on every rebuild: a reader whose habits reshuffled between
    /// visits would reasonably conclude the page is making it up.
    /// </summary>
    private static List<Group> Select(List<Group> candidates, int maxGroups)
    {
        var selected = new List<Group>();
        var facetUses = new Dictionary<int, int>();
        var namedAlone = new HashSet<int>();

        foreach (var candidate in candidates
                     .OrderByDescending(c => c.Score)
                     .ThenByDescending(c => c.Members.Count)
                     .ThenBy(c => c.Keys.Count)
                     .ThenBy(c => c.Keys[0])
                     .ThenBy(c => c.Keys[^1])
                     .Take(SelectionPool))
        {
            if (candidate.Keys.Any(k => facetUses.GetValueOrDefault(k) >= MaxGroupsPerFacet))
            {
                continue;
            }

            if (PairsNeedAnUnselectedHalf &&
                candidate.Keys.Count > 1 &&
                candidate.Keys.All(namedAlone.Contains))
            {
                continue;
            }

            if (selected.Any(s => Jaccard(s.Members, candidate.Members) > MaxOverlap))
            {
                continue;
            }

            selected.Add(candidate);
            foreach (var key in candidate.Keys)
            {
                facetUses[key] = facetUses.GetValueOrDefault(key) + 1;
            }

            if (candidate.Keys.Count == 1)
            {
                namedAlone.Add(candidate.Keys[0]);
            }

            if (selected.Count == maxGroups)
            {
                break;
            }
        }

        return selected;
    }

    /// <summary>
    /// What a pair says, which is its rarer half plus however much the other one narrowed it.
    ///
    /// <para>
    /// <paramref name="narrowest"/> is the smaller of the two facets' own member counts, and when
    /// the pair matches it the second name added nothing: every fake-relationship story the reader
    /// owns is a romance, so "Romance + Fake Relationship" is "Fake Relationship" with a longer
    /// label. Scoring it as its rarer half alone leaves the two tied, and the selection's tie-break
    /// on key count then keeps the shorter name. Without this a pair can always be built that
    /// outscores the single it is made of, and every group on the page grows a redundant second word.
    /// </para>
    /// </summary>
    private static double PairSpecificity(double a, double b, int members, int narrowest)
    {
        var narrowing = narrowest <= 0 ? 0 : 1 - ((double)members / narrowest);
        return Math.Max(a, b) + (PairMinorWeight * Math.Min(a, b) * narrowing);
    }

    /// <summary>
    /// Size under a square root, against specificity straight. Size is damped because a group twice
    /// as big is not twice as interesting past the point where it recurs at all, and specificity is
    /// not because that is the entire difference between this and counting tags.
    /// </summary>
    private static double Score(int members, double specificity) =>
        Math.Sqrt(members) * specificity;

    /// <summary>Overlap of two ascending member lists, as a linear merge rather than a set build.</summary>
    private static double Jaccard(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        int i = 0, j = 0, both = 0;
        while (i < a.Count && j < b.Count)
        {
            if (a[i] == b[j])
            {
                both++;
                i++;
                j++;
            }
            else if (a[i] < b[j])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return both == 0 ? 0 : (double)both / (a.Count + b.Count - both);
    }
}
