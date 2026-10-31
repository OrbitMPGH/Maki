namespace Maki.Metadata.Catalogue;

/// <summary>
/// A list of strings stored as one character array plus offsets. A hundred thousand short names
/// as separate strings cost a 22-byte object header each on top of their text, and a reference to
/// each from the array holding them; here they cost four bytes of offset.
/// </summary>
internal sealed class TextBlob
{
    public static readonly TextBlob Empty = new([]);

    private readonly char[] _chars;
    private readonly int[] _offsets;

    public TextBlob(IReadOnlyList<string> items)
    {
        _offsets = new int[items.Count + 1];
        for (var i = 0; i < items.Count; i++)
        {
            _offsets[i + 1] = _offsets[i] + items[i].Length;
        }

        _chars = new char[_offsets[^1]];
        for (var i = 0; i < items.Count; i++)
        {
            items[i].CopyTo(_chars.AsSpan(_offsets[i]));
        }
    }

    public int Count => _offsets.Length - 1;

    public ReadOnlySpan<char> this[int index] => _chars.AsSpan(_offsets[index], _offsets[index + 1] - _offsets[index]);

    public int LengthAt(int index) => _offsets[index + 1] - _offsets[index];
}
