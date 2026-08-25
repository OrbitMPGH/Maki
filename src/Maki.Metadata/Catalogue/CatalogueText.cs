using System.Globalization;
using System.Text;

namespace Maki.Metadata.Catalogue;

/// <summary>
/// Text primitives shared by the catalogue indexes: folding, tokenizing, and bounded edit distance.
///
/// <para>
/// The folding here is not a matter of taste. <see cref="Normalize"/> has to agree with the FTS5
/// tokenizer <c>MangaBakaDumpService.BuildSearchIndex</c> builds the title index with
/// (<c>unicode61 remove_diacritics 2</c>), because <see cref="FuzzyTermIndex"/> reads its
/// vocabulary straight out of that index and then compares it against tokens a user typed. If the
/// two disagree, every query token carrying a diacritic reads as one edit away from the term it
/// should have matched exactly, and the fuzzy budget is spent correcting a spelling that was
/// already right. <c>CatalogueTextTests</c> pins this by round-tripping a fixture through a real
/// FTS5 table.
/// </para>
/// </summary>
public static class CatalogueText
{
    /// <summary>
    /// Longest token this will run edit distance over. Beyond it the DP is not worth the cycles and
    /// the input is not a word anyone mistyped, it is a run of glued-together text.
    /// </summary>
    public const int MaxComparableLength = 128;

    /// <summary>
    /// Case-folded, diacritic-stripped, punctuation-collapsed form. Everything outside Unicode's
    /// letter and number categories becomes a single space, which is what unicode61 treats as a
    /// separator.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Runes, not chars: a CJK extension ideograph is a surrogate pair, and testing the halves
        // individually reports "not a letter" and would shred the token into spaces.
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            // FormD splits an accented letter into its base plus a combining mark; dropping the
            // marks is what "remove_diacritics" means.
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(Rune.ToLowerInvariant(rune));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>The normalized form split into tokens. Empty array for text that folds to nothing.</summary>
    public static string[] Tokenize(string? text)
    {
        var normalized = Normalize(text);
        return normalized.Length == 0
            ? []
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Tokens normalized, sorted and rejoined, so word order and punctuation stop mattering. This
    /// is the identity <see cref="CreditIndex"/> keys names by: "Ito, Junji", "Junji Ito" and
    /// "junji  ito" all reduce to <c>ito junji</c> and merge into one creator rather than three.
    /// </summary>
    public static string TokenSortKey(string? text)
    {
        var tokens = Tokenize(text);
        if (tokens.Length <= 1)
        {
            return tokens.Length == 0 ? string.Empty : tokens[0];
        }

        Array.Sort(tokens, StringComparer.Ordinal);
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// A <see cref="TokenSortKey"/> with romanized long vowels collapsed, so the same Japanese name
    /// spelled several ways lands on one key.
    ///
    /// <para>
    /// The dump carries both "Junji Itou" (83 works) and "ITO Junji" (1), which are one person
    /// written in wapuro and macron-less Hepburn. Without this, searching either spelling finds a
    /// fraction of the work. <c>ou</c>, <c>oo</c> and <c>uu</c> all collapse, since those are the
    /// three sequences that encode a long vowel; doubled consonants are deliberately left alone
    /// because they are phonemic ("Ippo" is not "Ipo").
    /// </para>
    /// <para>
    /// It is lossy on English words that happen to contain those pairs, so "Young" keys the same as
    /// "Yong". That is why this only ever picks which spelling of a resolved name to prefer, and
    /// never merges two names' works together: a wrong merge would be invisible, while a wrong
    /// preference just shows the other spelling's page.
    /// </para>
    /// </summary>
    public static string RomanizationKey(string? text)
    {
        var key = TokenSortKey(text);
        if (key.Length == 0)
        {
            return key;
        }

        var builder = new StringBuilder(key.Length);
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            builder.Append(c);
            if (i + 1 < key.Length &&
                ((c == 'o' && (key[i + 1] == 'u' || key[i + 1] == 'o')) ||
                 (c == 'u' && key[i + 1] == 'u')))
            {
                i++;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether a token may be fuzzy-expanded at all: ASCII letters and digits only.
    ///
    /// <para>
    /// This is the CJK guard, and it is the one that matters. unicode61 does not word-segment CJK,
    /// so 進撃の巨人 arrives as a single five-character token; one edit away from it is a different
    /// phrase, not a typo, and a length floor cannot tell the difference because the token clears
    /// any reasonable one. The same rule also excludes Cyrillic, Greek, Arabic and Thai, where the
    /// argument is weaker (those are alphabetic and do get segmented) but the traffic is small and
    /// one predicate beats a script table.
    /// </para>
    /// <para>
    /// It pays for itself twice: on the ASCII-only path a token's UTF-8 bytes map one to one onto
    /// its characters, so <see cref="BoundedDistance"/> can run over the packed term bytes and
    /// still be a character-level distance, and the term index never decodes a string it is about
    /// to reject.
    /// </para>
    /// </summary>
    public static bool IsExpandable(ReadOnlySpan<char> token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Restricted Damerau-Levenshtein (optimal string alignment) distance, giving up as soon as
    /// every alignment in flight already costs more than <paramref name="max"/> and returning
    /// <c>max + 1</c> to say so. Transpositions count as one edit because they are the most common
    /// typo a title search sees ("bersrek").
    ///
    /// <para>
    /// Full rows rather than a diagonal band: at token lengths a row is a dozen ints, the row
    /// minimum abort already skips the work a band would have skipped, and the banded index
    /// arithmetic is where this kind of routine goes subtly wrong.
    /// </para>
    ///
    /// <param name="scratch">
    /// Three rows of <c>b.Length + 1</c> ints. Passed in rather than stack-allocated because the
    /// caller runs this once per candidate term and zeroing a fresh buffer per call would cost more
    /// than the distance does.
    /// </param>
    /// </summary>
    public static int BoundedDistance<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, int max, Span<int> scratch)
        where T : IEquatable<T>
    {
        if (max < 0)
        {
            return 1;
        }

        // Length alone can settle it, and this is the test that rejects most candidates.
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        if (a.Length > MaxComparableLength || b.Length > MaxComparableLength)
        {
            return max + 1;
        }

        if (a.SequenceEqual(b))
        {
            return 0;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            var length = Math.Max(a.Length, b.Length);
            return length <= max ? length : max + 1;
        }

        var width = b.Length + 1;
        if (scratch.Length < width * 3)
        {
            throw new ArgumentException($"Scratch needs {width * 3} ints for a {b.Length}-byte target.", nameof(scratch));
        }

        var twoBack = scratch[..width];
        var oneBack = scratch.Slice(width, width);
        var current = scratch.Slice(width * 2, width);

        for (var j = 0; j <= b.Length; j++)
        {
            oneBack[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1].Equals(b[j - 1]) ? 0 : 1;
                var value = Math.Min(
                    Math.Min(current[j - 1] + 1, oneBack[j] + 1),
                    oneBack[j - 1] + cost);

                if (i > 1 && j > 1 && a[i - 1].Equals(b[j - 2]) && a[i - 2].Equals(b[j - 1]))
                {
                    value = Math.Min(value, twoBack[j - 2] + 1);
                }

                current[j] = value;
                if (value < rowMin)
                {
                    rowMin = value;
                }
            }

            // Every cell in this row is already over budget, and no later row can lower one, so no
            // alignment through here can come in under max.
            if (rowMin > max)
            {
                return max + 1;
            }

            var recycled = twoBack;
            twoBack = oneBack;
            oneBack = current;
            current = recycled;
        }

        var distance = oneBack[b.Length];
        return distance <= max ? distance : max + 1;
    }

    /// <summary>
    /// <see cref="BoundedDistance{T}(ReadOnlySpan{T}, ReadOnlySpan{T}, int, Span{int})"/> with its
    /// own scratch, for callers comparing one pair rather than scanning a dictionary.
    /// </summary>
    public static int BoundedDistance<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, int max)
        where T : IEquatable<T>
    {
        if (Math.Abs(a.Length - b.Length) > max || a.Length > MaxComparableLength || b.Length > MaxComparableLength)
        {
            return max + 1;
        }

        return BoundedDistance(a, b, max, new int[(b.Length + 1) * 3]);
    }

    /// <summary>Scratch big enough for any target up to <paramref name="maxTargetLength"/> bytes.</summary>
    public static int[] RentScratch(int maxTargetLength) => new int[(maxTargetLength + 1) * 3];

    /// <summary>
    /// Which distinct characters an ASCII token contains, as a bitmap: one bit per letter, one for
    /// "any digit", one for anything else.
    ///
    /// <para>
    /// This is the prefilter that makes <see cref="FuzzyTermIndex.Expand"/> usable. Every character
    /// present in one string and absent from the other costs at least one edit, so
    /// <see cref="MaskLowerBound"/> is a true lower bound on the distance and can reject a candidate
    /// with two popcounts instead of a full DP. Measured against the shipped dictionary it cuts the
    /// candidates that reach the DP by well over an order of magnitude, taking a token's expansion
    /// from tens of milliseconds to about one.
    /// </para>
    /// </summary>
    public static uint LetterMask(ReadOnlySpan<byte> ascii)
    {
        var mask = 0u;
        foreach (var c in ascii)
        {
            mask |= c switch
            {
                >= (byte)'a' and <= (byte)'z' => 1u << (c - 'a'),
                >= (byte)'A' and <= (byte)'Z' => 1u << (c - 'A'),
                >= (byte)'0' and <= (byte)'9' => 1u << 26,
                _ => 1u << 27,
            };
        }

        return mask;
    }

    /// <summary>Cheapest possible edit distance between two <see cref="LetterMask"/>s.</summary>
    public static int MaskLowerBound(uint a, uint b) =>
        Math.Max(
            System.Numerics.BitOperations.PopCount(a & ~b),
            System.Numerics.BitOperations.PopCount(b & ~a));
}
