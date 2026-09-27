using System.Text.RegularExpressions;

namespace Maki.Core.Quality;

/// <summary>
/// The regexes behind a pass of <see cref="QualityScorer"/> calls, each built once. A pattern that
/// times out once is treated as never matching for the rest of the pass, so one catastrophic pattern
/// costs a single timeout rather than one per file. Not thread-safe: make one per pass.
/// </summary>
public sealed class RegexCache(TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(200);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private readonly Dictionary<string, Regex?> _compiled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _timedOut = new(StringComparer.Ordinal);

    public bool HasTimedOut(string pattern) => _timedOut.Contains(pattern);

    /// <summary>Null for an invalid pattern or one that has timed out.</summary>
    public bool? IsMatch(string input, string pattern)
    {
        if (_timedOut.Contains(pattern))
        {
            return null;
        }

        if (!_compiled.TryGetValue(pattern, out var regex))
        {
            regex = TryBuild(pattern, _timeout);
            _compiled[pattern] = regex;
        }

        if (regex is null)
        {
            return null;
        }

        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            _timedOut.Add(pattern);
            return null;
        }
    }

    public static bool IsValid(string pattern) => TryBuild(pattern, DefaultTimeout) is not null;

    private static Regex? TryBuild(string pattern, TimeSpan timeout)
    {
        try
        {
            return new Regex(pattern, Options, timeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
