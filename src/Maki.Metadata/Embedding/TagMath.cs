using System.Buffers.Binary;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Weighted-tag channel for the hybrid scorer. MangaBaka's tags_v2 gives every tag a
/// categorical weight (core > defining > recurrent > incidental, plus unweighted); tags are
/// packed as (id:int32 LE, class:byte) pairs into a BLOB so the candidate scan never parses
/// JSON, and similarity is the cosine of the sparse IDF-weighted tag vectors of the seed
/// profile and a candidate. Pure and dependency-free, like <see cref="EmbeddingMath"/>.
/// </summary>
public static class TagMath
{
    // Weight classes are stored raw (not as numeric weights) so the numeric mapping can be
    // retuned without re-indexing.
    public const byte Unweighted = 0;
    public const byte Incidental = 1;
    public const byte Recurrent = 2;
    public const byte Defining = 3;
    public const byte Core = 4;

    private const int EntrySize = 5; // int32 id + byte class

    public static byte ClassOf(string? weight) => weight switch
    {
        "core" => Core,
        "defining" => Defining,
        "recurrent" => Recurrent,
        "incidental" => Incidental,
        _ => Unweighted,
    };

    /// <summary>Numeric strength of a weight class. Tunable without touching stored blobs.</summary>
    /// <summary>
    /// The <c>name_path</c> roots that describe what a story <em>is</em>, as opposed to what its
    /// cast looks like. Taken straight from MangaBaka's own taxonomy rather than invented here:
    /// "Themes &gt; Cohabitation" and "Themes &gt; Marriage &gt; Arranged Marriage" against
    /// "Character Traits &gt; Attractiveness &gt; Beautiful Female Lead" and "Character Archetype
    /// &gt; Dere Types &gt; Tsundere Female Lead".
    ///
    /// <para>
    /// This is the distinction IDF cannot make. Rarity is not kind: <c>Dense Male Lead</c> carries a
    /// higher IDF than <c>Cohabitation</c>, so a seed set chosen for one premise is outscored by
    /// candidates matching the trope tail it happens to share with its genre. Sexual Content, Work
    /// Info and Audience Demographics are excluded deliberately - they say who a series is for and
    /// how it was published, which is the least premise-bearing thing in the vocabulary.
    /// </para>
    ///
    /// <para>
    /// <c>Narrative Tropes</c> and <c>Locations</c> were in this set and were measured out of it.
    /// They read as story-bearing and are not: <c>Narrative Tropes &gt; Love Tropes &gt; Love
    /// Triangle</c> is the genre furniture the boost exists to demote, and <c>Locations &gt;
    /// Japan</c> is true of most of the catalogue. On three cohabitation seeds, boosting with them
    /// included promoted the generic romcom to first and reached 7 of 10 on-premise picks; without
    /// them the same boost put three cohabitation titles on top, dropped that romcom to fourth and
    /// reached 9 of 10. A category earns a place here by being about what happens, not by sounding
    /// like it.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> StoryCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Themes", "Settings", "Relationship", "Activities", "Occupations",
    };

    /// <summary>
    /// Whether a <c>name_path</c> root describes what a story is about. Exposed so the passage
    /// builder can pick premise tags with the same definition the scoring channel uses, rather than
    /// growing a second list that drifts from this one.
    /// </summary>
    public static bool IsStoryCategory(string? category) =>
        category is { Length: > 0 } && StoryCategories.Contains(category);

    /// <summary>
    /// The <c>name_path</c> roots that describe how a series is PACKAGED and who it is for, rather
    /// than what happens in it: which demographic it was drawn for, whether it is a longstrip webtoon
    /// or a tankoubon, a 4-koma, a doujinshi, full colour.
    ///
    /// <para>
    /// <see cref="StoryCategories"/> excludes these deliberately and correctly, for the question that
    /// boost was written to answer: they are the least premise-bearing thing in the vocabulary, and a
    /// seed set chosen for a premise should not be outscored by everything sharing its demographic.
    /// But "does this FEEL like what I read" is a different question from "is this about the same
    /// thing", and format is most of the answer to it - a webtoon returned for a tankoubon seed reads
    /// wrong however well the plot lines up.
    /// </para>
    ///
    /// <para>
    /// So this is a separate dial, not a widening of the story one. Sexual Content stays out of both:
    /// it is a content warning, and the content-rating filter already owns that decision.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> FormatCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Work Info", "Audience Demographics",
    };

    /// <summary>
    /// <paramref name="storyBoost"/> applied to a tag whose category describes the story,
    /// <paramref name="formatBoost"/> to one describing how it is packaged, 1 otherwise. An
    /// uncategorised tag - a vocabulary written before the column existed - weights at 1, so a stale
    /// index degrades to exactly the old behaviour rather than to a lopsided one.
    /// </summary>
    public static double CategoryWeight(string? category, double storyBoost, double formatBoost = 1.0)
    {
        if (category is not { Length: > 0 })
        {
            return 1.0;
        }

        if (storyBoost != 1.0 && StoryCategories.Contains(category))
        {
            return storyBoost;
        }

        // The two sets are disjoint by construction, so the order of these tests does not decide
        // anything - but a tag can only ever take one boost, which is why this is not a product.
        return formatBoost != 1.0 && FormatCategories.Contains(category) ? formatBoost : 1.0;
    }

    public static double ClassWeight(byte cls) => cls switch
    {
        Core => 1.0,
        Defining => 0.7,
        Recurrent => 0.4,
        Incidental => 0.15,
        _ => 0.35, // unweighted: the tagger hasn't rated it — assume mildly relevant
    };

    public static byte[] Pack(IReadOnlyList<(int Id, byte Class)> tags)
    {
        var blob = new byte[tags.Count * EntrySize];
        for (var i = 0; i < tags.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(i * EntrySize), tags[i].Id);
            blob[(i * EntrySize) + 4] = tags[i].Class;
        }

        return blob;
    }

    public static IReadOnlyList<(int Id, byte Class)> Unpack(byte[]? blob)
    {
        if (blob is null || blob.Length == 0 || blob.Length % EntrySize != 0)
        {
            return [];
        }

        var tags = new List<(int, byte)>(blob.Length / EntrySize);
        for (var i = 0; i < blob.Length; i += EntrySize)
        {
            tags.Add((BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(i)), blob[i + 4]));
        }

        return tags;
    }

    /// <summary>
    /// True when the packed blob contains at least one id from every group. Used for the
    /// tag filter: each selected tag name maps to a group of vocab ids (casing variants),
    /// and a candidate must carry all selected tags. Null/empty blob matches nothing.
    /// </summary>
    public static bool ContainsAll(byte[]? blob, IReadOnlyList<int[]> idGroups)
    {
        if (blob is null || blob.Length == 0 || blob.Length % EntrySize != 0)
        {
            return false;
        }

        foreach (var group in idGroups)
        {
            var found = false;
            for (var i = 0; i + EntrySize <= blob.Length && !found; i += EntrySize)
            {
                var id = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(i));
                for (var g = 0; g < group.Length; g++)
                {
                    if (group[g] == id)
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// MangaBaka's tag taxonomy, flattened into "which ancestors does this tag imply, and how much".
    ///
    /// <para>
    /// The vocabulary is 2,493 tags over a six-level tree with 17 roots, and a series carries a
    /// median of SEVEN of them. Matching those exactly is therefore sparse by construction: two
    /// series can both be fantasy-with-swords and share not one id, because one is tagged
    /// <c>Activities &gt; Physical Activities &gt; Swordplay</c> and the other
    /// <c>Activities &gt; Physical Activities &gt; Martial Arts</c>. Only the root segment was ever
    /// kept (as <see cref="TagInfo.Category"/>), so everything between root and leaf was thrown away.
    /// </para>
    ///
    /// <para>
    /// Crediting a tag's ancestors at a decaying weight fixes that. Measured over 4,000
    /// crowd-validated co-read pairs against 4,000 random pairs, the separation between them moves
    /// from Cohen's d 0.674 to 1.699 at <c>decay = 0.5</c>. That is a proxy, not an nDCG, and it is
    /// why the knob ships at 0 until the harness says otherwise.
    /// </para>
    ///
    /// <para>
    /// Ancestor nodes are keyed by their path PREFIX and given negative ids, disjoint from the
    /// positive tag ids, because most prefixes are not themselves tags: 16 of the 17 roots have no
    /// tag of their own. Negative ids are also what keeps the UI honest for free, since
    /// <c>SemanticRecommender</c> looks every matched id up in the vocabulary and drops what it
    /// cannot name, so an ancestor can never surface as a "matched tag".
    /// </para>
    /// </summary>
    public sealed class TagTree
    {
        private static readonly Ancestor[] None = [];

        private readonly Dictionary<int, Ancestor[]> _ancestors;

        private TagTree(Dictionary<int, Ancestor[]> ancestors) => _ancestors = ancestors;

        public static readonly TagTree Empty = new([]);

        public bool IsEmpty => _ancestors.Count == 0;

        public Ancestor[] AncestorsOf(int tagId) =>
            _ancestors.TryGetValue(tagId, out var found) ? found : None;

        /// <param name="Idf">
        /// The ancestor's OWN inverse document frequency, precomputed here rather than taken from
        /// the tag that implied it. That distinction is the whole correctness of this type: see the
        /// remarks on <see cref="Build"/>.
        /// </param>
        public readonly record struct Ancestor(int Id, double Decay, double Idf);

        /// <param name="activeCount">
        /// Size of the corpus the IDF is taken against, the same number the caller's own tag IDF
        /// uses. Ancestor weights have to sit on that identical scale or the two halves of the
        /// candidate vector are not comparable.
        /// </param>
        /// <param name="decay">
        /// Weight of a tag's parent relative to the tag, compounding per level. 0 disables the
        /// mechanism and returns <see cref="Empty"/>, which every consumer scores exactly as it did
        /// before this existed.
        /// </param>
        /// <param name="includeSelf">
        /// Also emit the tag's own full path as a node at weight 1. Without it a series tagged
        /// <c>Themes &gt; Romance</c> and one tagged <c>Themes &gt; Romance &gt; Harem</c> meet only
        /// at <c>Themes</c>, because the first carries Romance as a tag id and the second as a path
        /// prefix, and those are different keys. Measured worse (single-seed nDCG 0.132 against
        /// 0.137 for the same decay), so it is off: the extra meeting point is not worth counting
        /// every exact match twice.
        /// </param>
        /// <remarks>
        /// AN ANCESTOR MUST CARRY ITS OWN RARITY, NEVER ITS CHILDREN'S. Giving a prefix the mean IDF
        /// of the tags underneath it prices <c>Themes</c> at 6.17 when its true IDF is 0.00, because
        /// its children are individually rare and their parent is on almost everything. The error
        /// compounds with seed count, since more seeds reach more of the tree: measured, the
        /// child-IDF version improved single-seed nDCG 0.127 to 0.137 and simultaneously took
        /// whole-library nDCG 0.131 to 0.116 with median pick popularity collapsing from rank 1,233
        /// to 311, i.e. straight into a popularity chart. A prefix that is also a tag takes that
        /// tag's exact count; one that is not (16 of the 17 roots) takes the sum of its descendants,
        /// which over-counts a series carrying two sibling tags and is therefore a floor on the
        /// IDF rather than a ceiling. That direction is the safe one.
        /// </remarks>
        public static TagTree Build(
            IEnumerable<KeyValuePair<int, TagInfo>> vocab, double decay, bool includeSelf,
            long activeCount)
        {
            if (decay <= 0)
            {
                return Empty;
            }

            var paths = new List<(int Id, string[] Parts)>();
            var descendantCount = new Dictionary<string, long>(StringComparer.Ordinal);
            var exactCount = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (id, info) in vocab)
            {
                if (info.NamePath is not { Length: > 0 } path)
                {
                    continue;
                }

                var parts = path.Split(" > ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                // A prefix that is itself a tag knows exactly how many series carry it.
                exactCount[string.Join(" > ", parts)] = info.SeriesCount;

                if (parts.Length >= 2 || includeSelf)
                {
                    paths.Add((id, parts));
                }

                var last = includeSelf ? parts.Length : parts.Length - 1;
                for (var d = 1; d <= last; d++)
                {
                    var prefix = string.Join(" > ", parts, 0, d);
                    descendantCount[prefix] = descendantCount.GetValueOrDefault(prefix) + info.SeriesCount;
                }
            }

            // Ids are assigned in sorted order. They never leave the process, but a stable
            // assignment means two eval runs over the same vocabulary produce identical scores
            // rather than merely equivalent ones.
            var prefixId = new Dictionary<string, int>(descendantCount.Count, StringComparer.Ordinal);
            var prefixIdf = new Dictionary<string, double>(descendantCount.Count, StringComparer.Ordinal);
            var corpus = Math.Max(2, activeCount);
            foreach (var prefix in descendantCount.Keys.Order(StringComparer.Ordinal))
            {
                prefixId[prefix] = -(prefixId.Count + 1);
                var df = exactCount.TryGetValue(prefix, out var exact) && exact > 0
                    ? exact
                    : descendantCount[prefix];
                prefixIdf[prefix] = Math.Log((double)corpus / Math.Clamp(df, 1, corpus - 1));
            }

            var ancestors = new Dictionary<int, Ancestor[]>(paths.Count);
            foreach (var (id, parts) in paths)
            {
                var last = includeSelf ? parts.Length : parts.Length - 1;
                var entries = new Ancestor[last];
                for (var d = 1; d <= last; d++)
                {
                    var prefix = string.Join(" > ", parts, 0, d);
                    entries[d - 1] = new Ancestor(
                        prefixId[prefix], Math.Pow(decay, parts.Length - d), prefixIdf[prefix]);
                }

                ancestors[id] = entries;
            }

            return new TagTree(ancestors);
        }
    }

    /// <summary>
    /// Seed tag profile: tag id → mean class weight across the seeds times the tag's IDF,
    /// with the vector norm precomputed so scoring a candidate only touches its own tags.
    /// </summary>
    public sealed record Profile(IReadOnlyDictionary<int, double> IdfWeight, double Norm)
    {
        public bool IsEmpty => IdfWeight.Count == 0 || Norm <= 0;

        public static readonly Profile Empty = new(new Dictionary<int, double>(), 0);
    }

    /// <summary>Every seed counted equally, which is what a caller with no taste signal wants.</summary>
    public static Profile BuildProfile(IReadOnlyCollection<byte[]> seedBlobs, Func<int, double> idf) =>
        BuildProfile([.. seedBlobs.Select(blob => (blob, 1.0))], idf);

    /// <summary>
    /// The weighted version, so the tag channel sees the same taste the centroid does.
    ///
    /// <para>
    /// It matters more than it looks: <see cref="Score"/> carries the second-largest coefficient in
    /// <see cref="EmbeddingMath.HybridScore"/>, and until this overload existed the profile was a
    /// flat mean over the whole library — a seed the reader finished twice and a seed they opened
    /// once contributed identically, however hard the behavioural weighting had pushed them apart.
    /// </para>
    ///
    /// <para>
    /// Weights are relative only. The profile is normalized into a cosine, so scaling every seed by
    /// the same factor is a no-op and a caller does not have to care what scale it hands in.
    /// </para>
    /// </summary>
    public static Profile BuildProfile(
        IReadOnlyCollection<(byte[] Blob, double Weight)> seeds, Func<int, double> idf,
        double sharpening = 1.0, Func<int, double>? categoryWeight = null, double consensus = 1.0,
        TagTree? tree = null)
    {
        if (seeds.Count == 0)
        {
            return Profile.Empty;
        }

        var total = 0.0;
        foreach (var (_, weight) in seeds)
        {
            total += Math.Max(0, weight);
        }

        // Every seed weighted zero (or negative, which nothing produces but nothing forbids either)
        // leaves no profile to build; falling back to a flat mean would quietly ignore the caller.
        if (total <= 0)
        {
            return Profile.Empty;
        }

        var mean = new Dictionary<int, double>();
        // Ancestors are accumulated in their own pair of dictionaries rather than folded into
        // `mean`, because the idf applied below is looked up BY ID and an ancestor prefix has no id
        // in the vocabulary. `ancestorMass` is the same quantity `mean` holds (share-weighted class
        // weight, so consensus still means what it means); `ancestorIdf` carries the same mass again
        // multiplied by the idf and category weight of the CHILD tag that implied it. Their ratio is
        // the ancestor's effective idf, a mass-weighted average over whichever children fired.
        var ancestors = tree is { IsEmpty: false } ? tree : null;
        var ancestorMass = ancestors is null ? null : new Dictionary<int, double>();
        var ancestorIdf = ancestors is null ? null : new Dictionary<int, double>();

        foreach (var (blob, weight) in seeds)
        {
            var share = Math.Max(0, weight) / total;
            if (share <= 0)
            {
                continue;
            }

            foreach (var (id, cls) in Unpack(blob))
            {
                var mass = ClassWeight(cls) * share;
                mean[id] = mean.GetValueOrDefault(id) + mass;

                if (ancestors is null)
                {
                    continue;
                }

                // The category weight is the child's, and correctly so: a category is the ROOT of
                // the path, so every ancestor of a tag shares the tag's own category by
                // construction. The IDF is the ancestor's, which is the part that must not be
                // inherited (see TagTree.Build's remarks).
                var category = categoryWeight?.Invoke(id) ?? 1.0;
                foreach (var ancestor in ancestors.AncestorsOf(id))
                {
                    var decayed = mass * ancestor.Decay;
                    ancestorMass![ancestor.Id] = ancestorMass.GetValueOrDefault(ancestor.Id) + decayed;
                    ancestorIdf![ancestor.Id] =
                        ancestorIdf.GetValueOrDefault(ancestor.Id) + (decayed * ancestor.Idf * category);
                }
            }
        }

        var idfWeight = new Dictionary<int, double>(mean.Count);
        var normSq = 0.0;
        foreach (var (id, w) in mean)
        {
            // consensus reweights how many seeds agreed on a tag, and nothing else. w is already
            // the share of seed weight carrying this tag times its class weight, so it lives in
            // (0, 1] and a power above 1 pushes the tags only one seed had toward zero while
            // leaving a tag every seed had at 1. Four seeds that are all childhood-friend romcoms
            // share Childhood Friends, Romance, Comedy, Slice of Life and Heterosexual; a candidate
            // carrying the whole set is nearer what they have in common than one carrying the first
            // alone, and a linear profile prices those far too close together.
            //
            // Deliberately NOT the same operation as sharpening, which exponentiates this product
            // AFTER idf and category are folded in and so rewards rarity as much as agreement -
            // that is the half that drove it onto thinly tagged obscurities.
            var agreed = consensus == 1.0 ? w : Math.Pow(w, consensus);
            var v = agreed * idf(id) * (categoryWeight?.Invoke(id) ?? 1.0);
            // Sharpening concentrates the profile on the tags the seeds actually agree about. It is
            // needed because Score is a cosine over the candidate's whole tag list, which rewards
            // matching MANY profile tags rather than the top ones - and a seed set with a specific
            // premise still carries a large tail of generic character tropes that most of the genre
            // shares. Measured on three cohabitation seeds, the profile ranked Cohabitation and
            // Arranged Marriage first and fourth by weight, yet a candidate with neither outscored
            // three that had both, purely by matching Love Triangle, Tsundere and Partial Nudity
            // further down the same profile. Raising the weights to a power leaves the ordering
            // alone and widens the gaps, so the agreed-on tags dominate the dot product.
            idfWeight[id] = sharpening == 1.0 ? v : Math.Pow(v, sharpening);
            normSq += idfWeight[id] * idfWeight[id];
        }

        if (ancestorMass is not null)
        {
            foreach (var (ancestorId, mass) in ancestorMass)
            {
                if (mass <= 0)
                {
                    continue;
                }

                // Effective idf of the ancestor, and the reason the two dictionaries exist. At the
                // shipped consensus and sharpening of 1 the whole expression collapses to
                // `ancestorIdf[ancestorId]`, which is exactly the decayed sum that was measured.
                var effectiveIdf = ancestorIdf![ancestorId] / mass;
                var agreed = consensus == 1.0 ? mass : Math.Pow(mass, consensus);
                var v = agreed * effectiveIdf;
                var weighted = sharpening == 1.0 ? v : Math.Pow(v, sharpening);
                idfWeight[ancestorId] = weighted;
                normSq += weighted * weighted;
            }
        }

        return new Profile(idfWeight, Math.Sqrt(normSq));
    }

    /// <summary>
    /// Cosine ∈ [0,1] between the seed profile and a candidate's packed tags, both
    /// IDF-weighted. <paramref name="matched"/>, when given, receives every shared tag with
    /// its share of the dot product (unsorted) so the UI can rank matches by contribution.
    /// </summary>
    public static double Score(
        byte[]? candidateBlob, Profile profile, Func<int, double> idf,
        List<(int Id, double Contribution)>? matched = null, double candidateNormPower = 1.0,
        Func<int, double>? categoryWeight = null, TagTree? tree = null)
    {
        if (candidateBlob is null || candidateBlob.Length % EntrySize != 0 || profile.IsEmpty)
        {
            return 0;
        }

        if (tree is { IsEmpty: false } ancestors)
        {
            return ScoreExpanded(
                candidateBlob, profile, idf, matched, candidateNormPower, categoryWeight, ancestors);
        }

        var dot = 0.0;
        var candNormSq = 0.0;
        for (var i = 0; i + EntrySize <= candidateBlob.Length; i += EntrySize)
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(candidateBlob.AsSpan(i));
            // Applied to both sides, so this stays a cosine in a consistently reweighted space. On
            // the profile alone it would tilt the numerator while leaving candidate norms in the old
            // space, which is the asymmetry that made profile sharpening reward sparse candidates.
            var v = ClassWeight(candidateBlob[i + 4]) * idf(id) * (categoryWeight?.Invoke(id) ?? 1.0);
            candNormSq += v * v;
            if (profile.IdfWeight.TryGetValue(id, out var seedV))
            {
                var c = seedV * v;
                dot += c;
                matched?.Add((id, c));
            }
        }

        if (dot <= 0 || candNormSq <= 0)
        {
            return 0;
        }

        // candidateNormPower damps the candidate-side normalization. At 1 this is a plain cosine,
        // which makes a well-tagged series pay for every tag the seeds did not ask about: the norm
        // grows with the full list while the dot product only grows with the overlap. That is the
        // trade behind two separate complaints - a thinly tagged niche title beating a known one on
        // the same premise, and a candidate matching many middling profile tags beating one matching
        // the few that matter. Below 1 the penalty softens; at 0 the score is a raw dot product and
        // a series carrying every tag in the vocabulary would win everything.
        //
        // Not bounded by 1 any more once this drops below 1, so Weights.Tag is calibrated against a
        // different scale and has to be re-swept beside it rather than carried over.
        var candNorm = Math.Sqrt(candNormSq);
        return dot / (profile.Norm * (candidateNormPower == 1.0 ? candNorm : Math.Pow(candNorm, candidateNormPower)));
    }

    /// <summary>
    /// <see cref="Score"/> with the candidate's tags expanded into their taxonomy ancestors, so a
    /// candidate that shares no tag id with the profile can still match on what those tags are
    /// KINDS of.
    ///
    /// <para>
    /// Split out rather than folded into the main loop because it cannot avoid a dictionary: an
    /// ancestor is reachable from several of a candidate's tags at once, so its weight has to be
    /// accumulated before it can be squared into the norm. The single-pass version above stays the
    /// path taken whenever the knob is off, which is what keeps the default free.
    /// </para>
    /// </summary>
    private static double ScoreExpanded(
        byte[] candidateBlob, Profile profile, Func<int, double> idf,
        List<(int Id, double Contribution)>? matched, double candidateNormPower,
        Func<int, double>? categoryWeight, TagTree tree)
    {
        var expanded = new Dictionary<int, double>((candidateBlob.Length / EntrySize) * 4);
        for (var i = 0; i + EntrySize <= candidateBlob.Length; i += EntrySize)
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(candidateBlob.AsSpan(i));
            var mass = ClassWeight(candidateBlob[i + 4]);
            var category = categoryWeight?.Invoke(id) ?? 1.0;
            expanded[id] = expanded.GetValueOrDefault(id) + (mass * idf(id) * category);
            foreach (var ancestor in tree.AncestorsOf(id))
            {
                // The ancestor's own IDF, not a decayed copy of the tag's. A prefix on nearly every
                // series contributes nearly nothing however rare the tag that implied it.
                expanded[ancestor.Id] =
                    expanded.GetValueOrDefault(ancestor.Id) + (mass * ancestor.Idf * category * ancestor.Decay);
            }
        }

        var dot = 0.0;
        var candNormSq = 0.0;
        foreach (var (id, v) in expanded)
        {
            candNormSq += v * v;
            if (profile.IdfWeight.TryGetValue(id, out var seedV))
            {
                var c = seedV * v;
                dot += c;
                // Ancestor ids are negative and name nothing in the vocabulary. The caller building
                // the UI's matched-tag list looks every id up and drops what it cannot name, so they
                // are filtered there rather than here, where they still order the real tags right.
                matched?.Add((id, c));
            }
        }

        if (dot <= 0 || candNormSq <= 0)
        {
            return 0;
        }

        var norm = Math.Sqrt(candNormSq);
        return dot / (profile.Norm * (candidateNormPower == 1.0 ? norm : Math.Pow(norm, candidateNormPower)));
    }
}
