using System.Text.RegularExpressions;
using Maki.Core.Entities;

namespace Maki.Core.Images;

/// <summary>The release group name a notice's text suggests, and the Discord invite code it carries.</summary>
public sealed record CreditGroupGuess(string? Name, string? Invite)
{
    public static readonly CreditGroupGuess None = new(null, null);
}

/// <summary>
/// Reads a release group's name off the text OCR read from a credit page or ad. Signals, strongest first: a
/// "&lt;name&gt; scans/scanlations/translations" phrase on one line or split over lines, "translated by /
/// scanned by / brought to you by &lt;name&gt;", a web address that is not a generic service, and a handle on
/// Patreon or Ko-fi. Every text read for a notice votes, and OCR variants of one name are merged.
/// <para>
/// Ported from the git-ignored <c>research/credit-pages/2026-11-14-efficiency/groups</c> prototype
/// (<c>groupname.py</c>, the vote and merge in <c>evaluate.py</c>). On that library it named 729 of 826
/// ungrouped chapters but agreed with the known group only about two times in three (junk phrases, OCR noise,
/// aliases), so its answer is a suggestion for the user to confirm, never a chapter's group by itself.
/// </para>
/// </summary>
public static class CreditGroupName
{
    /// <summary>Bump when the output changes, so stored suggestions are read again.</summary>
    public const int Version = 1;

    private const string Kind = @"(scanlations?|scans?|translations?|tls?|comics|toons?|subs|team)";
    private const string Tld = @"(?:com|net|org|gg|xyz|to|co|io|me|moe|pm|site|online|fun|club|app|cc|us|info|blog)";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex Phrase = new(@"([A-Za-z0-9'’\-\. ]{2,40}?)[ \-]*" + Kind + @"\b", Options, Timeout);
    /// <summary>
    /// A line that is only the group word. Narrower than <see cref="Kind"/>: a lone "TL" or "Team" is a staff
    /// label, and the lines above it are names.
    /// </summary>
    private static readonly Regex KindOnly = new(@"^(scanlations?|scans?|translations?)$", Options, Timeout);
    private static readonly Regex AboveLine = new(@"^[A-Za-z0-9'’\-\. ]{2,20}$", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Fused = new(
        @"\b([A-Za-z][A-Za-z0-9'\-]{2,24}?)[\-]?(scans|scanlations|translations|toon|comics)\b", Options, Timeout);
    private static readonly Regex By = new(
        @"(?:translated|scanned|scanlated|typeset|brought to you|presented|uploaded|fan-scanlated)\s*by[:\s|]*([A-Za-z0-9'’\-\. ]{3,40})",
        Options, Timeout);
    private static readonly Regex TheOur = new(@"^(the|our)\b", Options, Timeout);
    private static readonly Regex Domain = new(
        @"([a-z0-9][a-z0-9\-]{2,40})\s*[\.:]\s*" + Tld + @"\b(?:\s*/\s*([A-Za-z0-9_\-]{3,30}))?", Options, Timeout);
    private static readonly Regex Blogspot = new(@"([a-z0-9\-]{3,30})\.blogspot", Options, Timeout);
    private static readonly Regex InviteCode = new(@"discord\s*\.\s*gg\s*/\s*\|?\s*([A-Za-z0-9]{6,12})", Options, Timeout);
    private static readonly Regex Edges = new(@"^[^A-Za-z0-9]+|[^A-Za-z0-9!]+$", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, Timeout);

    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "discord", "patreon", "ko-fi", "kofi", "paypal", "amazon", "bookwalker", "twitter", "x", "instagram", "facebook",
        "mangadex", "bato", "batoto", "reddit", "youtube", "tumblr", "pixiv", "t", "bit", "booth", "cdjapan", "ebookjapan",
        "renta", "kobo", "gmail", "google", "dsc", "linktr", "buymeacoffee", "tapas", "webtoons", "naver", "kakao", "comico",
        "mangaplus", "viz", "shueisha", "kodansha", "pocket", "comic-walker", "www", "https", "http", "co", "amzn", "gumroad",
        // Not in the prototype, which named "skythewood.blogspot.com" as "blogspot"; the blogspot pass reads the subdomain.
        "blogspot",
    };

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "our", "by", "of", "for", "at", "and", "on", "this", "us", "from", "more", "other", "fan", "official",
        "manga", "english", "is", "are", "join", "read", "support", "credits", "with", "in", "to", "a", "no", "your",
        "my", "new", "all", "these", "their", "quality", "free", "raw", "raws", "we", "you", "presented", "present",
    };

    /// <summary>
    /// The best name guess and invite code from every text read for one notice (null and blank texts are
    /// skipped, repeats count once). <paramref name="known"/> are names already settled elsewhere (groups the
    /// user confirmed): a guess within a small edit distance of one takes its spelling, so "QMEGA SCANS"
    /// becomes "Omega Scans" once someone has confirmed that.
    /// </summary>
    public static CreditGroupGuess Suggest(IEnumerable<string?> texts, IEnumerable<string>? known = null) =>
        Safely(() => SuggestCore(texts, known), CreditGroupGuess.None);

    private static CreditGroupGuess SuggestCore(IEnumerable<string?> texts, IEnumerable<string>? known)
    {
        var votes = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        var spellings = new Dictionary<string, List<(string Name, int Count)>>(StringComparer.Ordinal);
        var codes = new List<(string Code, int Count)>();

        foreach (var text in texts.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal))
        {
            foreach (var (score, name) in Names(text!))
            {
                var key = Key(name);
                if (!votes.ContainsKey(key))
                {
                    votes[key] = 0;
                    order.Add(key);
                    spellings[key] = [];
                }

                votes[key] += score;
                Bump(spellings[key], name);
            }

            if (Invite(text!) is { } code)
            {
                Bump(codes, code);
            }
        }

        var invite = Most(codes);
        if (order.Count == 0)
        {
            return new CreditGroupGuess(null, invite);
        }

        var settled = (known ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => (Key: Key(n), Name: n.Trim()))
            .Where(n => n.Key.Length >= 4)
            .DistinctBy(n => n.Key)
            .ToList();

        // Each key joins the first canonical key it is close to, most voted first, as the prototype did.
        var canon = new Dictionary<string, string>(StringComparer.Ordinal);
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in order.OrderByDescending(k => votes[k]))
        {
            if (settled.FirstOrDefault(s => s.Key == key || Close(key, s.Key)) is { Key: not null } match)
            {
                canon[key] = match.Key;
                named[match.Key] = match.Name;
                continue;
            }

            canon[key] = canon.Values.FirstOrDefault(c => !named.ContainsKey(c) && Close(key, c)) ?? key;
        }

        var merged = new List<(string Key, int Count)>();
        foreach (var key in order)
        {
            var target = canon[key];
            var at = merged.FindIndex(m => m.Key == target);
            if (at < 0)
            {
                merged.Add((target, votes[key]));
            }
            else
            {
                merged[at] = (target, merged[at].Count + votes[key]);
            }
        }

        var best = Most(merged)!;
        var display = named.TryGetValue(best, out var settledName) ? settledName : Most(spellings[best]);
        return new CreditGroupGuess(Fit(display), invite);
    }

    /// <summary>Scored name guesses read off one region's text (lines joined by " | "), best first.</summary>
    public static IReadOnlyList<(int Score, string Name)> Names(string text) => Safely(() => NamesCore(text), []);

    private static IReadOnlyList<(int Score, string Name)> NamesCore(string text)
    {
        var lines = text.Split(" | ");
        var found = Phrases(lines).Concat(ByLines(lines)).Concat(Domains(text));
        var best = new List<(int Score, string Name)>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (score, name) in found)
        {
            var key = Key(name);
            if (key.Length < 4)
            {
                continue;
            }

            if (!at.TryGetValue(key, out var index))
            {
                at[key] = best.Count;
                best.Add((score, name));
            }
            else if (score > best[index].Score)
            {
                best[index] = (score, name);
            }
        }

        // A stable sort, so equal scores keep the order they were found in.
        return best.OrderByDescending(b => b.Score).ToList();
    }

    /// <summary>The Discord invite code in the text, or null.</summary>
    public static string? Invite(string text) => Safely(() =>
    {
        var match = InviteCode.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }, null);

    /// <summary>
    /// <paramref name="work"/>, or <paramref name="fallback"/> when a pattern ran past its timeout on some
    /// pathological OCR text: a missing suggestion is better than a failed search or load.
    /// </summary>
    internal static T Safely<T>(Func<T> work, T fallback)
    {
        try
        {
            return work();
        }
        catch (RegexMatchTimeoutException)
        {
            return fallback;
        }
    }

    /// <summary>A name reduced to lowercase letters and digits, which is how two spellings of one name compare.</summary>
    public static string Key(string name) =>
        new(name.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());

    private static IEnumerable<(int, string)> Phrases(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var added = false;
            var match = Phrase.Match(line);
            if (match.Success)
            {
                var words = Spaces.Split(match.Groups[1].Value.Trim()).Where(w => w.Length > 0).ToList();
                // The trailing words, dropping the sentence words before them.
                var keep = new List<string>();
                for (var w = words.Count - 1; w >= 0; w--)
                {
                    if (Stop.Contains(words[w].ToLowerInvariant()) || keep.Count >= 4)
                    {
                        break;
                    }

                    keep.Add(words[w]);
                }

                if (keep.Count > 0)
                {
                    keep.Reverse();
                    added = true;
                    yield return (3, Clean(string.Join(' ', keep) + " " + match.Groups[2].Value));
                }
            }

            // "LEFT | FIELD | SCANS": the name sits on the lines above. The prototype only reached this when
            // the line also matched the phrase, which a lone "SCANS" never does, so this fires more often.
            if (!added && i > 0 && KindOnly.IsMatch(line))
            {
                var above = lines[Math.Max(0, i - 2)..i]
                    .Select(l => l.Trim())
                    .Where(l => AboveLine.IsMatch(l) && l.Any(char.IsLetter) && !l.Contains('.') &&
                        !Spaces.Split(l).Any(w => Stop.Contains(w.ToLowerInvariant())))
                    .ToList();
                if (above.Count > 0)
                {
                    yield return (2, Clean(string.Join(' ', above) + " " + line));
                }
            }

            // Fused: "OMEGASCANS", "KUUDERE-SCANS!".
            foreach (Match fused in Fused.Matches(line))
            {
                if (!Stop.Contains(fused.Groups[1].Value.ToLowerInvariant()))
                {
                    yield return (2, Clean(fused.Groups[1].Value + " " + fused.Groups[2].Value));
                }
            }
        }
    }

    private static IEnumerable<(int, string)> ByLines(string[] lines)
    {
        foreach (Match match in By.Matches(string.Join(" | ", lines)))
        {
            var name = Clean(match.Groups[1].Value);
            if (name.Length > 0 && !Stop.Contains(name.ToLowerInvariant()) && !TheOur.IsMatch(name))
            {
                yield return (2, name);
            }
        }
    }

    private static IEnumerable<(int, string)> Domains(string text)
    {
        foreach (Match match in Domain.Matches(text))
        {
            var host = match.Groups[1].Value.ToLowerInvariant();
            if (Generic.Contains(host))
            {
                if (host is "patreon" or "ko-fi" or "kofi" && match.Groups[2].Success)
                {
                    yield return (1, match.Groups[2].Value);
                }

                continue;
            }

            yield return (2, host);
        }

        foreach (Match match in Blogspot.Matches(text))
        {
            yield return (2, match.Groups[1].Value.ToLowerInvariant());
        }
    }

    private static string Clean(string name) => Spaces.Replace(Edges.Replace(name.Trim(), string.Empty), " ");

    /// <summary>Whether two keys are OCR variants of one name: a small edit distance between longer keys.</summary>
    internal static bool Close(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2 || Math.Min(a.Length, b.Length) < 6)
        {
            return false;
        }

        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[^1] <= (a.Length < 9 ? 1 : 2);
    }

    private static void Bump<T>(List<(T Value, int Count)> counts, T value) where T : notnull
    {
        var at = counts.FindIndex(c => EqualityComparer<T>.Default.Equals(c.Value, value));
        if (at < 0)
        {
            counts.Add((value, 1));
        }
        else
        {
            counts[at] = (value, counts[at].Count + 1);
        }
    }

    /// <summary>The value counted most, the first one counted on a tie.</summary>
    private static T? Most<T>(List<(T Value, int Count)> counts) where T : class
    {
        T? best = null;
        var most = 0;
        foreach (var (value, count) in counts)
        {
            if (count > most)
            {
                (best, most) = (value, count);
            }
        }

        return best;
    }

    private static string? Fit(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Length <= CreditNotice.MaxNameLength ? name : name[..CreditNotice.MaxNameLength].Trim();
}
