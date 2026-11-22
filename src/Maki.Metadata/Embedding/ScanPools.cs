using System.Buffers;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Pools for the catalogue-sized buffers a search or recommendation scan rents: a value per index
/// row, about half a megabyte each.
///
/// <para>
/// Not <c>ArrayPool.Shared</c>, which keeps up to 32 arrays per size in every per-core partition. A
/// whole-library recommendation rents a channel per seed query, so one request left about 33 of
/// them reachable (17 MB) for the life of the process. These keep four per size; a request that
/// needs more allocates the rest and lets the collector have them back.
/// </para>
/// </summary>
internal static class ScanPools
{
    private const int MaxLength = 1 << 20;
    private const int PerBucket = 4;

    private static volatile ArrayPool<float> _floats = ArrayPool<float>.Create(MaxLength, PerBucket);
    private static volatile ArrayPool<int> _ints = ArrayPool<int>.Create(MaxLength, PerBucket);
    private static volatile ArrayPool<double> _doubles = ArrayPool<double>.Create(MaxLength, PerBucket);

    public static ArrayPool<float> Floats => _floats;

    public static ArrayPool<int> Ints => _ints;

    public static ArrayPool<double> Doubles => _doubles;

    /// <summary>Replaces the pools so idle LOH scan buffers are freed on the next collect.</summary>
    public static void Reset()
    {
        _floats = ArrayPool<float>.Create(MaxLength, PerBucket);
        _ints = ArrayPool<int>.Create(MaxLength, PerBucket);
        _doubles = ArrayPool<double>.Create(MaxLength, PerBucket);
    }
}
