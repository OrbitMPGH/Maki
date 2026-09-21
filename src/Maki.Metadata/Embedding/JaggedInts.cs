namespace Maki.Metadata.Embedding;

/// <summary>
/// A per-row list of ints held as one flat payload plus row offsets, rather than one array per row.
///
/// <para>
/// The index carries three of these - genres, authors and artists - and at catalogue scale they are
/// mostly overhead rather than data: 126,865 rows times three columns times a 24-byte object header
/// and an 8-byte reference is about 12 MB of the 14.7 MB those columns occupied, for payloads
/// averaging two or three ints each. Flattened they cost the payload plus one offset per row.
/// </para>
///
/// <para>
/// Tag blobs are deliberately NOT stored this way, even though they have the same shape. Their
/// payload is variable-length and every consumer goes through <c>TagMath</c>, so flattening them
/// means changing that signature everywhere for a saving a third the size of this one.
/// </para>
/// </summary>
public sealed class JaggedInts
{
    private readonly int[] _values;
    private readonly int[] _offsets;

    private JaggedInts(int[] values, int[] offsets)
    {
        _values = values;
        _offsets = offsets;
    }

    /// <summary>An instance with no rows, for a column an index does not carry.</summary>
    public static JaggedInts Empty { get; } = new([], [0]);

    public int Count => _offsets.Length - 1;

    /// <summary>This row's values. Empty for a row that has none; never null.</summary>
    public ReadOnlySpan<int> this[int row] =>
        _values.AsSpan(_offsets[row], _offsets[row + 1] - _offsets[row]);

    /// <summary>
    /// Flattens one array per row. A null row is kept as an empty one, which is what every
    /// consumer already treated it as.
    /// </summary>
    public static JaggedInts From(IReadOnlyList<int[]?> rows)
    {
        var offsets = new int[rows.Count + 1];
        var total = 0;
        for (var row = 0; row < rows.Count; row++)
        {
            offsets[row] = total;
            total += rows[row]?.Length ?? 0;
        }

        offsets[rows.Count] = total;

        var values = new int[total];
        for (var row = 0; row < rows.Count; row++)
        {
            rows[row]?.CopyTo(values, offsets[row]);
        }

        return new JaggedInts(values, offsets);
    }
}
