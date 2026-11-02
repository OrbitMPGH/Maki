namespace Maki.Metadata.Embedding;

/// <summary>
/// Every row's packed tags (<see cref="TagMath"/>) in one byte array with an offset per row. As a
/// separate <c>byte[]</c> per row the 128k rows cost a 24-byte object header and an 8-byte
/// reference each, about 4 MB on top of the tags themselves. A row with no tags is an empty slice.
/// </summary>
public sealed class PackedTags
{
    private readonly byte[] _data;
    private readonly int[] _offsets;

    public PackedTags(byte[] data, int[] offsets)
    {
        _data = data;
        _offsets = offsets;
    }

    public int Count => _offsets.Length - 1;

    public ReadOnlySpan<byte> this[int row] => _data.AsSpan(_offsets[row], _offsets[row + 1] - _offsets[row]);

    /// <summary>Packs one blob per row, a null blob meaning no tags.</summary>
    public static PackedTags From(IReadOnlyList<byte[]?> blobs)
    {
        var offsets = new int[blobs.Count + 1];
        for (var i = 0; i < blobs.Count; i++)
        {
            offsets[i + 1] = offsets[i] + (blobs[i]?.Length ?? 0);
        }

        var data = new byte[offsets[^1]];
        for (var i = 0; i < blobs.Count; i++)
        {
            blobs[i]?.CopyTo(data, offsets[i]);
        }

        return new PackedTags(data, offsets);
    }
}
