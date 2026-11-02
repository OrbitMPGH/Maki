using System.Globalization;
using System.Text.RegularExpressions;

namespace Maki.Sources.Common;

/// <summary>
/// English relative dates: "2 hours ago", and the bare "1 month" some sites print without the "ago".
/// Months are 30 days and years 365, so these are never precise enough to count as release dates.
/// </summary>
public static partial class RelativeDate
{
    [GeneratedRegex(@"^(\d+|an?|one)\s+(min|mins|minute|minutes|hour|hours|hr|hrs|day|days|week|weeks|month|months|year|years)(?:\s+ago)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static DateTime? Parse(string? text, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Pattern().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var count = match.Groups[1].Value.ToLowerInvariant() switch
        {
            "a" or "an" or "one" => 1,
            var digits => int.Parse(digits, CultureInfo.InvariantCulture)
        };

        TimeSpan? delta = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(count),
            "hour" or "hours" or "hr" or "hrs" => TimeSpan.FromHours(count),
            "day" or "days" => TimeSpan.FromDays(count),
            "week" or "weeks" => TimeSpan.FromDays(count * 7),
            "month" or "months" => TimeSpan.FromDays(count * 30),
            "year" or "years" => TimeSpan.FromDays(count * 365),
            _ => null
        };

        return delta is null ? null : (now ?? DateTime.UtcNow) - delta.Value;
    }
}
