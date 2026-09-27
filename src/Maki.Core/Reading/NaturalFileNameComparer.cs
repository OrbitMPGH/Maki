namespace Maki.Core.Reading;

/// <summary>
/// Orders file names the way a person reads them: digit runs compare by numeric value
/// ("2" &lt; "10"), not lexically ("10" &lt; "2"). Text between digit runs compares
/// case-insensitively (invariant culture is not involved anywhere - this only ever looks at
/// ASCII digits and does an ordinal character comparison otherwise).
/// <para>
/// Two names that differ only in how much a digit run is padded ("1.jpg" vs "01.jpg") carry the
/// same numeric value, so the shorter run sorts first; this keeps the order deterministic instead
/// of falling back to input order for that pair. Anything left over after digit values and
/// padding agree - typically nothing, since equal padded value implies equal text on both sides -
/// is settled by a final ordinal comparison, so equal-under-this-comparer implies exactly equal.
/// </para>
/// </summary>
public sealed class NaturalFileNameComparer : StringComparer
{
    public static readonly NaturalFileNameComparer Instance = new();

    private NaturalFileNameComparer() { }

    public override int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            var cx = x[i];
            var cy = y[j];
            if (char.IsAsciiDigit(cx) && char.IsAsciiDigit(cy))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

                var digitsX = TrimLeadingZeros(x.AsSpan(startX, i - startX));
                var digitsY = TrimLeadingZeros(y.AsSpan(startY, j - startY));

                var byValue = digitsX.Length != digitsY.Length
                    ? digitsX.Length.CompareTo(digitsY.Length)
                    : digitsX.CompareTo(digitsY, StringComparison.Ordinal);
                if (byValue != 0) return byValue;

                // Same numeric value: less padding ("1") sorts before more ("01"), so a run of
                // equal-value entries with mixed padding still has one deterministic order.
                var byPadding = (i - startX).CompareTo(j - startY);
                if (byPadding != 0) return byPadding;

                continue;
            }

            var byChar = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
            if (byChar != 0) return byChar;
            i++;
            j++;
        }

        if (i < x.Length) return 1;
        if (j < y.Length) return -1;

        // Reached only when x and y agree on every digit value, every padding length and every
        // character up to case - i.e. they differ at most by case. Ordinal breaks that tie so the
        // comparer is a strict total order (equal iff exactly equal), matching Equals below.
        return string.CompareOrdinal(x, y);
    }

    private static ReadOnlySpan<char> TrimLeadingZeros(ReadOnlySpan<char> digits)
    {
        var k = 0;
        while (k < digits.Length - 1 && digits[k] == '0') k++;
        return digits[k..];
    }

    public override bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

    public override int GetHashCode(string obj) => Ordinal.GetHashCode(obj);
}
