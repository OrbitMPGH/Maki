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

    public static Profile BuildProfile(IReadOnlyCollection<byte[]> seedBlobs, Func<int, double> idf)
    {
        if (seedBlobs.Count == 0)
        {
            return Profile.Empty;
        }

        var mean = new Dictionary<int, double>();
        foreach (var blob in seedBlobs)
        {
            foreach (var (id, cls) in Unpack(blob))
            {
                mean[id] = mean.GetValueOrDefault(id) + (ClassWeight(cls) / seedBlobs.Count);
            }
        }

        var idfWeight = new Dictionary<int, double>(mean.Count);
        var normSq = 0.0;
        foreach (var (id, w) in mean)
        {
            var v = w * idf(id);
            idfWeight[id] = v;
            normSq += v * v;
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
        List<(int Id, double Contribution)>? matched = null)
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
            var v = ClassWeight(candidateBlob[i + 4]) * idf(id);
            candNormSq += v * v;
            if (profile.IdfWeight.TryGetValue(id, out var seedV))
            {
                var c = seedV * v;
                dot += c;
                matched?.Add((id, c));
            }
        }

        return dot <= 0 || candNormSq <= 0 ? 0 : dot / (profile.Norm * Math.Sqrt(candNormSq));
    }
}
