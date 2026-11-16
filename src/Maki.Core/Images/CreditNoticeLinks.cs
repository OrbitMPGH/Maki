using System.Text.RegularExpressions;

namespace Maki.Core.Images;

/// <summary>A notice's OCR text and the titles of the series it appears in, whose words say nothing about the group.</summary>
public sealed record CreditLinkText(int Id, string? Text, IReadOnlyCollection<string> Titles);

/// <summary>
/// Notices already together before any link: one name cluster, or a single notice with no name. <see cref="Anchor"/>
/// is the key of a confirmed or propagated name; two nodes with different anchors never join. <see cref="Named"/> is
/// whether the node has a name at all, suggested included.
/// </summary>
public sealed record CreditLinkNode(IReadOnlyList<int> Notices, string? Anchor, bool Named);

/// <summary>
/// Finds notices of one release group whose names OCR read differently or not at all, by the text they share.
/// A group's end cards change art every chapter, and OCR rarely reads a stylised logo, but they keep a handle, a
/// staff line or a sign-off ("Have a Good One.", "tl: scrubb", a border of "on a whim"). On the 2026-11-16 scan
/// (git-ignored <c>research/credit-pages/2026-11-16-group-linking</c>) a group went from 3.6 list entries on average to
/// 1.55, at 6 of 759 notices misplaced, and to 1.41 at 6 of 919 over small libraries of eight series. A logo match on
/// the images was measured too and left out: it joined two groups only, and its wrong links were official end cards
/// shared by two groups of one series.
/// </summary>
public static partial class CreditNoticeLinks
{
    /// <summary>The shortest piece of text compared, in letters and digits.</summary>
    public const int GramLength = 5;

    /// <summary>The cosine at which two notices link.</summary>
    public const double LinkAt = 0.35;

    /// <summary>
    /// The mean cosine over every pair across two buckets a link joins. One strong pair is not enough: Dungeon
    /// Crawler Carl's official card, which two groups both carry, would otherwise chain one group's notices into the other's.
    /// </summary>
    public const double JoinMean = 0.1;

    /// <summary>
    /// <see cref="JoinMean"/> when both buckets already have a name, suggested or not. Two names OCR read are two groups
    /// more often than one: in a small library Kayn and ELF Toon joined over Dungeon Crawler Carl's card at 0.1.
    /// </summary>
    public const double NamedJoinMean = 0.2;

    /// <summary>Pairs below this are dropped; they add almost nothing to a mean.</summary>
    private const double Kept = 0.1;

    /// <summary>Lines naming the original work's staff or publisher, which every group translating the series repeats.</summary>
    [GeneratedRegex(
        @"originalstory|storyby|artby|originalwork|leadart|scriptwrit|lineart|backgroundart|colou?rist|adaptation|original:|studio|publish|allrights|copyright|©|illustrat|author:|artist:|writer:|planning|production",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex Official();

    /// <summary>
    /// Words six or more of the scan's 66 groups write ("discord", "typesetter", "recruiting"). The text is cut at them
    /// rather than compared through them: a small library has too few notices for their weight to fall on its own.
    /// </summary>
    private static readonly string[] Generic =
    [
        .. new[]
        {
            "discord", "support", "translator", "typesetter", "quality", "proofreader", "cleaner", "redrawer", "translators", "server",
            "recruiting", "original", "patreon", "website", "translation", "translations", "typesetters", "redrawers", "release",
            "releases", "official", "mangadex", "looking", "available", "contact", "editors", "translated", "korean", "checker",
            "faster", "credits", "really", "continue", "manhwa", "proofreaders", "updates", "reading", "joinour", "interested",
            "thanks", "supportus", "experience", "typesetting", "editor", "ourwebsite", "cleaners", "qualitychecker", "ourdiscord",
            "supporting", "chapter", "chapters", "series", "please", "author",
        }.OrderByDescending(w => w.Length).ThenBy(w => w, StringComparer.Ordinal),
    ];

    private static readonly Regex GenericWords = new(string.Join('|', Generic), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>Pairwise cosine between notices, over the pairs at or above <see cref="Kept"/>.</summary>
    public sealed class Similarities
    {
        private readonly Dictionary<int, Dictionary<int, double>> _near;

        internal Similarities(Dictionary<int, Dictionary<int, double>> near) => _near = near;

        public double Of(int a, int b) => _near.TryGetValue(a, out var row) && row.TryGetValue(b, out var s) ? s : 0;

        public IReadOnlyDictionary<int, double> Near(int id) =>
            _near.TryGetValue(id, out var row) ? row : (IReadOnlyDictionary<int, double>)new Dictionary<int, double>();

        /// <summary>Every kept pair once, strongest first, ties by id.</summary>
        public IEnumerable<(int A, int B, double Similarity)> Pairs() => _near
            .SelectMany(r => r.Value.Where(p => r.Key < p.Key).Select(p => (A: r.Key, B: p.Key, Similarity: p.Value)))
            .OrderByDescending(p => p.Similarity).ThenBy(p => p.A).ThenBy(p => p.B);
    }

    /// <summary>
    /// TF-IDF over the five-letter pieces of each notice's text, without lines naming the original work's staff and
    /// without pieces of its series' titles. A piece only one notice has cannot link anything and is left out.
    /// </summary>
    public static Similarities Measure(IEnumerable<CreditLinkText> notices)
    {
        var grams = notices
            .Where(n => !string.IsNullOrWhiteSpace(n.Text))
            .DistinctBy(n => n.Id)
            .Select(n => (n.Id, Counts: Grams(n.Text!, n.Titles)))
            .Where(n => n.Counts.Count > 0)
            .ToList();
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, counts) in grams)
        {
            foreach (var g in counts.Keys)
            {
                df[g] = df.GetValueOrDefault(g) + 1;
            }
        }

        // Each notice becomes a row of (posting list, weight); a pair's cosine is summed row by row over shared pieces.
        var total = (double)grams.Count;
        var postings = new Dictionary<string, List<(int Row, double Weight)>>(StringComparer.Ordinal);
        var rows = new List<(int Id, List<(List<(int Row, double Weight)> Posting, double Weight)> Pieces)>();
        foreach (var (id, counts) in grams)
        {
            var weights = counts.Where(c => df[c.Key] > 1).Select(c => (c.Key, W: c.Value * Math.Log(total / df[c.Key]))).ToList();
            var norm = Math.Sqrt(weights.Sum(w => w.W * w.W));
            if (norm <= 0)
            {
                continue;
            }

            var pieces = new List<(List<(int Row, double Weight)>, double)>(weights.Count);
            foreach (var (gram, w) in weights)
            {
                if (!postings.TryGetValue(gram, out var list))
                {
                    postings[gram] = list = [];
                }

                list.Add((rows.Count, w / norm));
                pieces.Add((list, w / norm));
            }

            rows.Add((id, pieces));
        }

        var near = new Dictionary<int, Dictionary<int, double>>();
        var sums = new double[rows.Count];
        var touched = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            foreach (var (posting, w) in rows[i].Pieces)
            {
                // Rows were appended in order, so every later row in a posting list sits after this one's entry.
                for (var k = posting.Count - 1; k >= 0 && posting[k].Row > i; k--)
                {
                    var j = posting[k].Row;
                    if (sums[j] == 0)
                    {
                        touched.Add(j);
                    }

                    sums[j] += w * posting[k].Weight;
                }
            }

            foreach (var j in touched)
            {
                if (sums[j] >= Kept)
                {
                    Row(rows[i].Id)[rows[j].Id] = sums[j];
                    Row(rows[j].Id)[rows[i].Id] = sums[j];
                }

                sums[j] = 0;
            }

            touched.Clear();
        }

        return new Similarities(near);

        Dictionary<int, double> Row(int id) => near.TryGetValue(id, out var row) ? row : near[id] = [];
    }

    /// <summary>
    /// Joins nodes over their notices' links, strongest first (<see cref="LinkAt"/>): two nodes join when their anchors
    /// do not differ and the mean cosine over every pair of notices across them is at least <see cref="JoinMean"/>, or
    /// <see cref="NamedJoinMean"/> when both are named.
    /// Returns the nodes' indexes, one list per bucket, in the order of their first node.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Join(IReadOnlyList<CreditLinkNode> nodes, Similarities similarities)
    {
        var parent = Enumerable.Range(0, nodes.Count).ToArray();
        var anchor = nodes.Select(n => n.Anchor).ToArray();
        var named = nodes.Select(n => n.Named).ToArray();
        var notices = nodes.Select(n => n.Notices.ToList()).ToArray();
        var nodeOf = new Dictionary<int, int>();
        for (var i = 0; i < nodes.Count; i++)
        {
            foreach (var id in nodes[i].Notices)
            {
                nodeOf.TryAdd(id, i);
            }
        }

        foreach (var (a, b, s) in similarities.Pairs())
        {
            if (s < LinkAt)
            {
                break;
            }

            if (!nodeOf.TryGetValue(a, out var na) || !nodeOf.TryGetValue(b, out var nb))
            {
                continue;
            }

            var (ra, rb) = (Find(na), Find(nb));
            if (ra == rb || (anchor[ra] is { } x && anchor[rb] is { } y && x != y))
            {
                continue;
            }

            var (small, large) = notices[ra].Count <= notices[rb].Count ? (ra, rb) : (rb, ra);
            var cross = 0.0;
            foreach (var id in notices[small])
            {
                foreach (var (other, sim) in similarities.Near(id))
                {
                    if (nodeOf.TryGetValue(other, out var n) && Find(n) == large)
                    {
                        cross += sim;
                    }
                }
            }

            if (cross / ((double)notices[small].Count * notices[large].Count) < (named[small] && named[large] ? NamedJoinMean : JoinMean))
            {
                continue;
            }

            parent[small] = large;
            anchor[large] ??= anchor[small];
            named[large] |= named[small];
            notices[large].AddRange(notices[small]);
            notices[small] = [];
        }

        return Enumerable.Range(0, nodes.Count)
            .GroupBy(Find)
            .OrderBy(g => g.Min())
            .Select(g => (IReadOnlyList<int>)g.Order().ToList())
            .ToList();

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }
    }

    /// <summary>The five-letter pieces of a notice's text, counted, with official lines and title pieces left out.</summary>
    public static Dictionary<string, int> Grams(string text, IEnumerable<string> titles)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal);
        foreach (var title in titles)
        {
            var key = CreditGroupName.Key(title);
            for (var i = 0; i + GramLength <= key.Length; i++)
            {
                skip.Add(key.Substring(i, GramLength));
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in text.Split(" | "))
        {
            var key = CreditGroupName.Key(line);
            if (IsOfficial(line) || IsOfficial(key))
            {
                continue;
            }

            foreach (var piece in Pieces(key))
            {
                for (var i = 0; i + GramLength <= piece.Length; i++)
                {
                    var gram = piece.Substring(i, GramLength);
                    if (!skip.Contains(gram))
                    {
                        counts[gram] = counts.GetValueOrDefault(gram) + 1;
                    }
                }
            }
        }

        return counts;
    }

    private static string[] Pieces(string key)
    {
        try
        {
            return GenericWords.Split(key);
        }
        catch (RegexMatchTimeoutException)
        {
            return [key];
        }
    }

    private static bool IsOfficial(string line)
    {
        try
        {
            return Official().IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }
}
