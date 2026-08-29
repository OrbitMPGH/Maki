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
    /// <paramref name="boost"/> applied to a tag whose category describes the story, 1 otherwise.
    /// An uncategorised tag - a vocabulary written before the column existed - weights at 1, so a
    /// stale index degrades to exactly the old behaviour rather than to a lopsided one.
    /// </summary>
    public static double CategoryWeight(string? category, double boost) =>
        boost != 1.0 && category is { Length: > 0 } && StoryCategories.Contains(category) ? boost : 1.0;

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
        double sharpening = 1.0, Func<int, double>? categoryWeight = null, double consensus = 1.0)
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
        foreach (var (blob, weight) in seeds)
        {
            var share = Math.Max(0, weight) / total;
            if (share <= 0)
            {
                continue;
            }

            foreach (var (id, cls) in Unpack(blob))
            {
                mean[id] = mean.GetValueOrDefault(id) + (ClassWeight(cls) * share);
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
        Func<int, double>? categoryWeight = null)
    {
        if (candidateBlob is null || candidateBlob.Length % EntrySize != 0 || profile.IsEmpty)
        {
            return 0;
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
}
