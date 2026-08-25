using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Catalogue;

/// <summary>
/// Which credit a name holds on a series. "Studio" in the UI is <see cref="Publisher"/>: MangaBaka
/// carries publishers, not studios, and they are the nearest thing manga has.
/// </summary>
[Flags]
public enum CreditRole : byte
{
    None = 0,
    Author = 1,
    Artist = 2,
    Publisher = 4,
    Creator = Author | Artist,
    Any = Author | Artist | Publisher,
}

/// <summary>A resolved credit: which name, how far off the query was, and how much they made.</summary>
public readonly record struct CreditMatch(int NameId, int Distance, int WorkCount);

/// <summary>
/// Every author, artist and publisher in the dump, in memory, with the series each one is credited
/// on. This is what lets a search answer "junji ito" and what a creator page reads.
///
/// <para>
/// It is held in RAM rather than indexed into the dump because the whole thing is cheap: one scan
/// of <c>series</c> reads 275,197 rows in 2.9 s and yields 906,463 (name, series) pairs over
/// 128,841 distinct names and 1.5 MB of name text. Building it here means the dump's prepare step
/// is untouched and the dumps people already have on disk need no migration.
/// </para>
///
/// <para>
/// Credits are parsed with <c>System.Text.Json</c>, never in SQL. SQLite's <c>json_each</c> throws
/// <c>malformed JSON</c> on some rows of these columns while <c>System.Text.Json</c> read all
/// 275,197 without a single failure, so a SQL-side parse would silently drop creators.
/// </para>
///
/// <para>
/// Names are keyed by <see cref="CatalogueText.RomanizationKey"/>, not by their raw text, so word
/// order, punctuation and romanized long vowels stop splitting one person into several. "Ito,
/// Junji", "Junji Ito" and "Junji Itou" are one creator with one work list rather than three with a
/// fraction each.
/// </para>
///
/// <para>
/// That merge is measured, not assumed. Against the shipped dump it collapses 1,414 names out of
/// 111,631, and the largest merges are all one person written two ways: Gou Nagai with NAGAI Go
/// (250 works between them), Shoutarou Ishinomori with Ishinomori Shotaro (245), Takao Saitou with
/// SAITO Takao (107), Junji Itou with ITO Junji (84). Without it, opening Berserk and clicking
/// "MIURA Kentaro" led to a Kentarou Miura page that did not list Berserk, because the two
/// spellings held different halves of his bibliography. The known cost is that two genuinely
/// different people whose names differ only by a long vowel would merge; single-token pseudonyms
/// like "Yuuki" and "Yuki" are the realistic case, and they were already ambiguous.
/// </para>
///
/// <para>
/// Works are stored in popularity order, which is what a creator page wants by default and what
/// makes capping a huge publisher's list principled rather than arbitrary.
/// </para>
/// </summary>
public sealed class CreditIndex
{
    /// <summary>Popularity rank standing in for "the dump does not know", so it sorts last.</summary>
    private const int UnknownPopularity = int.MaxValue;

    public static readonly CreditIndex Empty = new(
        [], [], [], [], [0], [], [], new Dictionary<string, int>());

    private readonly string[] _names;

    /// <summary>Display names folded once at build time, so autocomplete is a scan and not 129k re-folds per keystroke.</summary>
    private readonly string[] _normalized;

    private readonly string[] _keys;
    private readonly CreditRole[] _roles;

    /// <summary>Concatenated work lists; name <c>i</c> owns <c>[_workOffsets[i], _workOffsets[i+1])</c>.</summary>
    private readonly int[] _workOffsets;
    private readonly long[] _works;

    /// <summary>The role held on each entry of <see cref="_works"/>, so <c>artist:</c> can exclude writing credits.</summary>
    private readonly CreditRole[] _workRoles;

    private readonly Dictionary<string, int> _byKey;

    private CreditIndex(
        string[] names, string[] normalized, string[] keys, CreditRole[] roles,
        int[] workOffsets, long[] works, CreditRole[] workRoles,
        Dictionary<string, int> byKey)
    {
        _names = names;
        _normalized = normalized;
        _keys = keys;
        _roles = roles;
        _workOffsets = workOffsets;
        _works = works;
        _workRoles = workRoles;
        _byKey = byKey;
    }

    public int NameCount => _names.Length;

    public int PairCount => _works.Length;

    public bool IsEmpty => _names.Length == 0;

    public string NameAt(int nameId) => _names[nameId];

    public CreditRole RolesAt(int nameId) => _roles[nameId];

    /// <summary>
    /// Reads every credit out of the dump. One scan, one pass of parsing, no SQL JSON.
    /// </summary>
    public static CreditIndex Build(SqliteConnection conn, ILogger logger, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;

        var byKey = new Dictionary<string, int>(160_000, StringComparer.Ordinal);
        var names = new List<string>(160_000);
        var keys = new List<string>(160_000);
        var roles = new List<CreditRole>(160_000);
        var entries = new List<Entry>(1_000_000);
        // How often each raw spelling of each merged name appears, so the display name is the one
        // the catalogue mostly uses rather than whichever row happened to be read first.
        var spellingCounts = new Dictionary<(int NameId, string Spelling), int>();
        var bestSpelling = new List<int>(160_000);
        var rows = 0;

        using (var scan = conn.CreateCommand())
        {
            scan.CommandText = """
                SELECT id, authors, artists, publishers, popularity_global_current
                FROM series
                WHERE state = 'active' AND type != 'novel'
                """;
            scan.CommandTimeout = 600;
            using var reader = scan.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows++;

                var seriesId = reader.GetInt64(0);
                var popularity = reader.IsDBNull(4) ? UnknownPopularity : reader.GetInt32(4);

                Collect(reader.IsDBNull(1) ? null : reader.GetString(1), CreditRole.Author, ParseNames);
                Collect(reader.IsDBNull(2) ? null : reader.GetString(2), CreditRole.Artist, ParseNames);
                Collect(reader.IsDBNull(3) ? null : reader.GetString(3), CreditRole.Publisher, ParsePublisherNames);

                void Collect(string? json, CreditRole role, Func<string?, List<string>> parse)
                {
                    foreach (var raw in parse(json))
                    {
                        var key = CatalogueText.RomanizationKey(raw);
                        if (key.Length == 0)
                        {
                            continue;
                        }

                        var spelling = raw.Trim();
                        if (!byKey.TryGetValue(key, out var nameId))
                        {
                            nameId = names.Count;
                            byKey[key] = nameId;
                            names.Add(spelling);
                            keys.Add(key);
                            roles.Add(CreditRole.None);
                            bestSpelling.Add(0);
                        }

                        var seen = spellingCounts.GetValueOrDefault((nameId, spelling)) + 1;
                        spellingCounts[(nameId, spelling)] = seen;
                        if (seen > bestSpelling[nameId])
                        {
                            bestSpelling[nameId] = seen;
                            names[nameId] = spelling;
                        }

                        roles[nameId] |= role;
                        entries.Add(new Entry(nameId, seriesId, popularity, role));
                    }
                }
            }
        }

        if (entries.Count == 0)
        {
            logger.LogInformation("No credits in the dump ({Rows} series scanned)", rows);
            return Empty;
        }

        // Group by name, then dedupe (name, series) so somebody credited as both writer and artist
        // on one title is one work with two roles rather than two works.
        var packed = entries.ToArray();
        Array.Sort(packed, static (a, b) =>
        {
            var byName = a.NameId.CompareTo(b.NameId);
            return byName != 0 ? byName : a.SeriesId.CompareTo(b.SeriesId);
        });

        var merged = 0;
        for (var i = 0; i < packed.Length; i++)
        {
            if (merged > 0 &&
                packed[merged - 1].NameId == packed[i].NameId &&
                packed[merged - 1].SeriesId == packed[i].SeriesId)
            {
                packed[merged - 1] = packed[merged - 1] with { Role = packed[merged - 1].Role | packed[i].Role };
                continue;
            }

            packed[merged++] = packed[i];
        }

        var workOffsets = new int[names.Count + 1];
        var works = new long[merged];
        var workRoles = new CreditRole[merged];

        var cursor = 0;
        var nextName = 0;
        while (cursor < merged)
        {
            var nameId = packed[cursor].NameId;
            var end = cursor;
            while (end < merged && packed[end].NameId == nameId)
            {
                end++;
            }

            // Popularity rank ascending, 1 being the most popular, unknown last. This is the order
            // a creator page shows and the order a cap keeps the head of.
            Array.Sort(packed, cursor, end - cursor, PopularityOrder.Instance);

            // Names with no credits cannot happen, but the offsets still have to cover every id.
            while (nextName <= nameId)
            {
                workOffsets[nextName++] = cursor;
            }

            for (var i = cursor; i < end; i++)
            {
                works[i] = packed[i].SeriesId;
                workRoles[i] = packed[i].Role;
            }

            cursor = end;
        }

        while (nextName <= names.Count)
        {
            workOffsets[nextName++] = merged;
        }

        logger.LogInformation(
            "Built the credit index: {Names} names over {Pairs} credits from {Rows} series in {Elapsed:F1}s",
            names.Count, merged, rows, (DateTime.UtcNow - started).TotalSeconds);

        var normalized = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            normalized[i] = CatalogueText.Normalize(names[i]);
        }

        return new CreditIndex(
            [.. names], normalized, [.. keys], [.. roles], workOffsets, works, workRoles, byKey);
    }

    /// <summary>How many works this name holds in any of <paramref name="roles"/>.</summary>
    public int WorkCountOf(int nameId, CreditRole roles = CreditRole.Any)
    {
        var from = _workOffsets[nameId];
        var to = _workOffsets[nameId + 1];
        if (roles == CreditRole.Any)
        {
            return to - from;
        }

        var count = 0;
        for (var i = from; i < to; i++)
        {
            if ((_workRoles[i] & roles) != 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>This name's MangaBaka series ids, most popular first.</summary>
    public long[] WorksOf(int nameId, CreditRole roles = CreditRole.Any)
    {
        var from = _workOffsets[nameId];
        var to = _workOffsets[nameId + 1];
        if (roles == CreditRole.Any)
        {
            return _works[from..to];
        }

        var kept = new List<long>(to - from);
        for (var i = from; i < to; i++)
        {
            if ((_workRoles[i] & roles) != 0)
            {
                kept.Add(_works[i]);
            }
        }

        return [.. kept];
    }

    /// <summary>
    /// Exact match on the merged name key, so word order, punctuation and romanized long vowels do
    /// not matter.
    /// </summary>
    public bool TryResolve(string? name, CreditRole roles, out int nameId)
    {
        nameId = -1;
        var key = CatalogueText.RomanizationKey(name);
        if (key.Length == 0 || !_byKey.TryGetValue(key, out var candidate))
        {
            return false;
        }

        if ((_roles[candidate] & roles) == 0)
        {
            return false;
        }

        nameId = candidate;
        return true;
    }

    /// <summary>
    /// <see cref="TryResolve"/>, then the nearest name within <paramref name="maxDistance"/> edits
    /// if that missed, so <c>author:junji itoo</c> still lands. Ties break on work count, since the
    /// creator somebody is more likely to have meant is the one with more to find.
    /// </summary>
    public bool TryResolveFuzzy(string? name, CreditRole roles, int maxDistance, out int nameId)
    {
        if (TryResolve(name, roles, out nameId))
        {
            return true;
        }

        nameId = -1;
        if (maxDistance <= 0 || IsEmpty)
        {
            return false;
        }

        var key = CatalogueText.RomanizationKey(name);
        if (key.Length == 0 || key.Length > CatalogueText.MaxComparableLength)
        {
            return false;
        }

        var scratch = CatalogueText.RentScratch(key.Length);
        var bestDistance = maxDistance + 1;
        var bestWorks = -1;

        for (var i = 0; i < _keys.Length; i++)
        {
            if ((_roles[i] & roles) == 0 || Math.Abs(_keys[i].Length - key.Length) > maxDistance)
            {
                continue;
            }

            var distance = CatalogueText.BoundedDistance<char>(_keys[i], key, maxDistance, scratch);
            if (distance > maxDistance)
            {
                continue;
            }

            var works = _workOffsets[i + 1] - _workOffsets[i];
            if (distance < bestDistance || (distance == bestDistance && works > bestWorks))
            {
                bestDistance = distance;
                bestWorks = works;
                nameId = i;
            }
        }

        return nameId >= 0;
    }

    /// <summary>
    /// The longest run of consecutive <paramref name="tokens"/> that names a credit.
    ///
    /// <para>
    /// Longest wins, and that is the whole point. "uzumaki junji ito" contains the run "junji ito",
    /// which resolves, while the full three-token run does not, so the query keeps its title word
    /// for the dense and lexical channels and still finds the author. A rule that only matched the
    /// whole query would miss it.
    /// </para>
    ///
    /// <para>
    /// A run shorter than <paramref name="minRunTokens"/> only counts when it is the entire query,
    /// and that rule is not optional. The dump credits people whose whole name is one ordinary
    /// word: there is a creator called "Akira" with 33 works and one called "Winter" with one, so
    /// without it "akira otomo" pulls in a stranger's bibliography instead of finding the manga,
    /// and "girls camping alone in winter near mount fuji" quietly acquires a credit channel. A
    /// single-token query is still allowed to name somebody, since that is all the user gave us.
    /// </para>
    /// </summary>
    public bool TryMatchLongestRun(
        IReadOnlyList<string> tokens, CreditRole roles, int minRunChars, int minRunTokens,
        out CreditMatch match, out int runStart, out int runLength)
    {
        match = default;
        runStart = 0;
        runLength = 0;

        for (var length = tokens.Count; length >= 1; length--)
        {
            if (length < minRunTokens && length != tokens.Count)
            {
                break;
            }

            for (var start = 0; start + length <= tokens.Count; start++)
            {
                var chars = 0;
                for (var i = start; i < start + length; i++)
                {
                    chars += tokens[i].Length;
                }

                if (chars < minRunChars)
                {
                    continue;
                }

                var run = string.Join(' ', tokens.Skip(start).Take(length));
                if (!TryResolve(run, roles, out var nameId))
                {
                    continue;
                }

                match = new CreditMatch(nameId, 0, WorkCountOf(nameId, roles));
                runStart = start;
                runLength = length;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Names to offer for a partly typed one, best first. A linear pass over ~129k short strings,
    /// which is a couple of milliseconds and cheaper than carrying a prefix structure that only
    /// this endpoint would use.
    /// </summary>
    public IReadOnlyList<CreditMatch> Suggest(string? query, CreditRole roles, int limit)
    {
        var needle = CatalogueText.Normalize(query);
        if (needle.Length == 0 || IsEmpty)
        {
            return [];
        }

        var hits = new List<(int NameId, int Rank, int Works)>();
        for (var i = 0; i < _names.Length; i++)
        {
            if ((_roles[i] & roles) == 0)
            {
                continue;
            }

            var normalized = _normalized[i];

            // Two tiers only, not one per match shape. Finer tiers rank an exact but obscure name
            // above the prolific one somebody almost certainly meant: the dump holds a one-work
            // creator called "Junji" and it was beating Junji Itou's 83, and "Urasawa Bunny" was
            // beating Naoki Urasawa because it happened to match at the start of the string.
            int rank;
            if (normalized.StartsWith(needle, StringComparison.Ordinal) ||
                normalized.Contains(' ' + needle, StringComparison.Ordinal))
            {
                rank = 0;
            }
            else if (normalized.Contains(needle, StringComparison.Ordinal))
            {
                rank = 1;
            }
            else
            {
                continue;
            }

            hits.Add((i, rank, _workOffsets[i + 1] - _workOffsets[i]));
        }

        hits.Sort(static (a, b) =>
        {
            var byRank = a.Rank.CompareTo(b.Rank);
            return byRank != 0 ? byRank : b.Works.CompareTo(a.Works);
        });

        return hits
            .Take(Math.Max(1, limit))
            .Select(h => new CreditMatch(h.NameId, h.Rank, WorkCountOf(h.NameId, roles)))
            .ToList();
    }

    /// <summary><c>authors</c> and <c>artists</c> are plain JSON string arrays.</summary>
    private static List<string> ParseNames(string? json)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return names;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return names;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { } name &&
                    !string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (JsonException)
        {
            // One unreadable column of one row, not a reason to lose the row's other credits.
        }

        return names;
    }

    /// <summary>
    /// <c>publishers</c> holds objects (<c>{"name","note","type"}</c>), and occasionally bare
    /// strings. Mirrors <c>MangaBakaLocalStore.ParsePublishers</c>, which surfaces the same names on
    /// the detail card.
    /// </summary>
    private static List<string> ParsePublisherNames(string? json)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return names;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return names;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Object when element.TryGetProperty("name", out var property)
                        && property.ValueKind == JsonValueKind.String => property.GetString(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (JsonException)
        {
        }

        return names;
    }

    private readonly record struct Entry(int NameId, long SeriesId, int Popularity, CreditRole Role);

    private sealed class PopularityOrder : IComparer<Entry>
    {
        public static readonly PopularityOrder Instance = new();

        public int Compare(Entry a, Entry b)
        {
            var byPopularity = a.Popularity.CompareTo(b.Popularity);
            return byPopularity != 0 ? byPopularity : a.SeriesId.CompareTo(b.SeriesId);
        }
    }

    /// <summary>Role name as it appears on the wire, for the API and the creator page.</summary>
    public static string RoleLabel(CreditRole role) => role switch
    {
        CreditRole.Author => "author",
        CreditRole.Artist => "artist",
        CreditRole.Publisher => "studio",
        _ => role.ToString().ToLowerInvariant(),
    };

    /// <summary>Parses a wire role name. Unknown or missing means every role.</summary>
    public static CreditRole ParseRole(string? role) =>
        role?.Trim().ToLowerInvariant() switch
        {
            "author" => CreditRole.Author,
            "artist" => CreditRole.Artist,
            "studio" or "publisher" => CreditRole.Publisher,
            "creator" => CreditRole.Creator,
            _ => CreditRole.Any,
        };

    /// <summary>The roles a name holds, as wire labels, for a creator header.</summary>
    public IReadOnlyList<string> RoleLabelsAt(int nameId)
    {
        var held = _roles[nameId];
        var labels = new List<string>(3);
        foreach (var role in new[] { CreditRole.Author, CreditRole.Artist, CreditRole.Publisher })
        {
            if ((held & role) != 0)
            {
                labels.Add(RoleLabel(role));
            }
        }

        return labels;
    }
}
