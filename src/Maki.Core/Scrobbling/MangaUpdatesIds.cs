namespace Maki.Core.Scrobbling;

/// <summary>
/// MangaUpdates addresses a series by an int64 <c>series_id</c> in the API and by the same number in
/// lowercase base36 on the website (<c>/series/pb8uwds/one-piece</c> is 55099564912). Maki stores the
/// slug, which is what <c>Series.MangaUpdatesId</c> and the MangaBaka dump hold, and converts at the
/// HTTP boundary only. Legacy numeric ids (<c>series.html?id=</c>) are a different numbering and are
/// never accepted.
/// </summary>
public static class MangaUpdatesIds
{
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";

    public static string ToSlug(long seriesId)
    {
        if (seriesId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seriesId));
        }

        Span<char> buffer = stackalloc char[13];
        var i = buffer.Length;
        while (seriesId > 0)
        {
            buffer[--i] = Digits[(int)(seriesId % 36)];
            seriesId /= 36;
        }

        return new string(buffer[i..]);
    }

    public static bool TryToSeriesId(string? slug, out long seriesId)
    {
        seriesId = 0;
        if (string.IsNullOrEmpty(slug) || slug.Length > 13)
        {
            return false;
        }

        long value = 0;
        try
        {
            foreach (var ch in slug)
            {
                var digit = Digits.IndexOf(char.ToLowerInvariant(ch));
                if (digit < 0)
                {
                    return false;
                }

                value = checked(value * 36 + digit);
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        if (value <= 0)
        {
            return false;
        }

        seriesId = value;
        return true;
    }

    /// <summary>
    /// A slug from user input: a <c>mangaupdates.com/series/{slug}</c> URL, or a bare slug. A bare value
    /// must contain a letter, so a pasted legacy numeric id is refused rather than read as base36.
    /// </summary>
    public static bool TryParse(string? input, out string slug)
    {
        slug = "";
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            return false;
        }

        if (text.All(char.IsAsciiLetterOrDigit))
        {
            if (!text.Any(char.IsAsciiLetter) || !TryToSeriesId(text, out _))
            {
                return false;
            }

            slug = text.ToLowerInvariant();
            return true;
        }

        if (ScrobbleMatching.ParseWebLinks([text]).TryGetValue("mangaupdates", out var linked) &&
            TryToSeriesId(linked, out _))
        {
            slug = linked;
            return true;
        }

        return false;
    }
}
