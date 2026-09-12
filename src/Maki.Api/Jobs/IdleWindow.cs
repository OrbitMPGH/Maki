using System.Globalization;

namespace Maki.Api.Jobs;

/// <summary>
/// Parses the "unload after this many idle minutes" environment variables the idle jobs share.
/// </summary>
public static class IdleWindow
{
    /// <summary>
    /// Minutes of idleness before the thing goes. Zero disables the unload; anything unparseable or
    /// negative falls back to <paramref name="defaultMinutes"/> rather than being read as "never",
    /// since a typo in an environment variable should not silently pin hundreds of megabytes on a
    /// machine that was configured to give them back.
    /// </summary>
    public static int Resolve(string? value, int defaultMinutes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultMinutes;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes >= 0
                ? minutes
                : defaultMinutes;
    }
}
